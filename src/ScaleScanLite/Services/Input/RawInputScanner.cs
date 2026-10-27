using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using ScaleScanLite.Core.Logic;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Infrastructure;
using static ScaleScanLite.Services.Input.RawInputNative;

namespace ScaleScanLite.Services.Input;

/// <summary>
/// Nghe máy quét barcode USB (chế độ bàn phím) bằng Windows Raw Input, chỉ từ đúng thiết bị đã chọn.
/// Máy quét vẫn gõ vào cửa sổ đang được chọn như bình thường (ví dụ app khác); Scale Pro chỉ nhận thêm một bản sao của mã,
/// không chặn và không gửi lại phím. Phím từ các bàn phím khác bị bỏ qua hoàn toàn (không lưu, không ghi log).
/// </summary>
internal sealed class RawInputScanner : IDisposable
{
    private sealed class DeviceState
    {
        public bool Shift;
        public required ScanBurstAssembler Assembler { get; init; }
    }

    private readonly Dictionary<IntPtr, DeviceState> _states = new();
    private readonly Dictionary<IntPtr, string> _paths = new();
    private readonly Func<ushort, ushort, bool, char?> _translate;
    private HwndSource? _source;
    private ScannerSettings _config = new();
    private bool _learning;
    private bool _registered;

    public RawInputScanner(Func<ushort, ushort, bool, char?>? translator = null)
        => _translate = translator ?? KeyTranslator.Translate;

    /// <summary>(mã quét được, mã nhận dạng máy quét).</summary>
    public event Action<string, string>? CodeScanned;

    /// <summary>(mã nhận dạng máy quét, mã thử) - chỉ phát ra trong chế độ nhận diện.</summary>
    public event Action<string, string>? DeviceLearned;

    public bool IsLearning => _learning;

    /// <summary>Đang đăng ký nhận phím từ Windows (chỉ khi đã bật + chọn thiết bị, hoặc đang nhận diện).</summary>
    internal bool IsRegistered => _registered;

    /// <summary>Tạo cửa sổ ẩn nhận thông điệp WM_INPUT. Phải gọi trên luồng giao diện.</summary>
    public void Start()
    {
        if (_source != null)
            return;

        var parameters = new HwndSourceParameters("ScaleScanLite.RawInput")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE: cửa sổ chỉ nhận thông điệp, không hiển thị
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        UpdateRegistration();
        AppLog.Info("Đã khởi tạo bộ nghe máy quét (Raw Input)");
    }

    public void Configure(ScannerSettings settings)
    {
        _config = new ScannerSettings
        {
            Enabled = settings.Enabled,
            DeviceId = settings.DeviceId,
            MaxCharGapMs = settings.MaxCharGapMs,
            MinLength = settings.MinLength
        };

        _states.Clear();
        UpdateRegistration();
    }

    /// <summary>Chờ lượt quét kế tiếp từ bất kỳ bàn phím nào rồi báo thiết bị đó qua <see cref="DeviceLearned"/>.</summary>
    public void StartLearning()
    {
        _states.Clear();
        _learning = true;
        UpdateRegistration();
    }

    public void StopLearning()
    {
        if (!_learning)
            return;

        _learning = false;
        _states.Clear();
        UpdateRegistration();
    }

    // ------------------------------------------------------------------
    // Xử lý một phím từ một thiết bị cụ thể (tách riêng để kiểm thử được)
    // ------------------------------------------------------------------
    internal void ProcessKey(IntPtr device, string devicePath, ushort virtualKey, ushort scanCode, bool isBreak, long nowMs)
    {
        bool configured = _config.Enabled && ScannerDeviceId.Matches(_config.DeviceId, devicePath);
        if (!_learning && !configured)
            return; // không phải máy quét đã chọn: bỏ qua, không đọc, không lưu

        if (!_states.TryGetValue(device, out var state))
        {
            state = new DeviceState { Assembler = new ScanBurstAssembler(_config.MaxCharGapMs, _config.MinLength) };
            _states[device] = state;
        }

        if (virtualKey is VK_SHIFT or VK_LSHIFT or VK_RSHIFT)
        {
            state.Shift = !isBreak;
            return;
        }

        if (isBreak || virtualKey == 0xFF)
            return;

        var ch = _translate(virtualKey, scanCode, state.Shift);
        if (ch is null)
            return;

        var code = state.Assembler.Add(ch.Value, nowMs);
        if (code is null)
            return;

        var id = ScannerDeviceId.FromDevicePath(devicePath);

        if (_learning)
        {
            _learning = false;
            _states.Clear();
            UpdateRegistration();
            DeviceLearned?.Invoke(id, code);
            return;
        }

        CodeScanned?.Invoke(code, id);
    }

    // ------------------------------------------------------------------
    // Windows
    // ------------------------------------------------------------------
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT)
        {
            try
            {
                HandleRawInput(lParam);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Lỗi khi xử lý dữ liệu từ máy quét: " + ex.Message);
            }
        }

        return IntPtr.Zero;
    }

    private void HandleRawInput(IntPtr rawInputHandle)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        if (GetRawInputData(rawInputHandle, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
            return;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInputHandle, RID_INPUT, buffer, ref size, headerSize) != size)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.Type != RIM_TYPEKEYBOARD)
                return;

            var keyboard = Marshal.PtrToStructure<RAWKEYBOARD>(buffer + (int)headerSize);
            ProcessKey(header.Device, DevicePath(header.Device), keyboard.VKey, keyboard.MakeCode,
                (keyboard.Flags & RI_KEY_BREAK) != 0, Environment.TickCount64);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private string DevicePath(IntPtr device)
    {
        if (_paths.TryGetValue(device, out var cached))
            return cached;

        uint chars = 0;
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, null, ref chars);
        if (chars == 0)
            return "";

        var name = new StringBuilder((int)chars);
        GetRawInputDeviceInfo(device, RIDI_DEVICENAME, name, ref chars);
        var path = name.ToString();
        _paths[device] = path;
        return path;
    }

    /// <summary>Chỉ đăng ký nhận phím khi thật sự cần (đã bật và chọn thiết bị, hoặc đang nhận diện); tắt thì gỡ đăng ký.</summary>
    private void UpdateRegistration()
    {
        if (_source == null)
            return;

        bool needed = _learning || (_config.Enabled && !string.IsNullOrWhiteSpace(_config.DeviceId));
        if (needed == _registered)
            return;

        var device = new[]
        {
            new RAWINPUTDEVICE
            {
                UsagePage = 0x01,   // Generic Desktop
                Usage = 0x06,       // Keyboard
                Flags = needed ? RIDEV_INPUTSINK : RIDEV_REMOVE,
                Target = needed ? _source.Handle : IntPtr.Zero
            }
        };

        if (RegisterRawInputDevices(device, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            _registered = needed;
            AppLog.Info(needed ? "Bắt đầu nghe máy quét USB" : "Đã ngừng nghe máy quét USB");
        }
        else
        {
            AppLog.Warn("Không đăng ký được Raw Input, lỗi Win32 " + Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        _learning = false;
        _config = new ScannerSettings();
        UpdateRegistration();

        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}
