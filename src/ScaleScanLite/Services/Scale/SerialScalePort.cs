using System.IO.Ports;
using System.Text;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Infrastructure;

namespace ScaleScanLite.Services.Scale;

/// <summary>
/// Đọc cân qua cổng COM bằng một luồng nền: tự mở cổng, tự nối lại khi rút/cắm.
/// Tự tách dòng theo \r hoặc \n nên không phụ thuộc cân gửi kết thúc dòng kiểu nào.
/// </summary>
internal sealed class SerialScalePort : IScalePort
{
    private readonly object _writeLock = new();
    private CancellationTokenSource? _cts;
    private Thread? _thread;
    private SerialPort? _port;
    private volatile bool _connected;

    public event Action<string>? LineReceived;

    public event Action<bool, string>? ConnectionChanged;

    public bool IsConnected => _connected;

    public void Start(ScaleSettings settings)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var snapshot = new ScaleSettings
        {
            PortName = settings.PortName,
            BaudRate = settings.BaudRate,
            RequestCommand = settings.RequestCommand,
            PollIntervalMs = settings.PollIntervalMs
        };

        _thread = new Thread(() => Run(snapshot, token)) { IsBackground = true, Name = "ScaleSerial" };
        _thread.Start();
    }

    public void Stop()
    {
        var cts = _cts;
        var thread = _thread;
        _cts = null;
        _thread = null;

        if (cts == null)
            return;

        cts.Cancel();
        thread?.Join(TimeSpan.FromSeconds(3));
        cts.Dispose();
        SetConnected(false, "Đã đóng cổng cân");
    }

    public bool Send(string command)
    {
        lock (_writeLock)
        {
            var port = _port;
            if (port is not { IsOpen: true })
                return false;

            try
            {
                port.Write(command + "\r\n");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Gửi lệnh tới cân thất bại: " + ex.Message);
                return false;
            }
        }
    }

    private void Run(ScaleSettings s, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var port = new SerialPort(s.PortName, s.BaudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 1000
                };

                port.Open();
                lock (_writeLock) _port = port;
                SetConnected(true, $"{s.PortName} đã kết nối");

                ReadLoop(port, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                SetConnected(false, $"Không mở được {s.PortName}: {ex.Message}");
            }
            finally
            {
                lock (_writeLock) _port = null;
            }

            if (_connected)
                SetConnected(false, $"Mất kết nối {s.PortName}");

            ct.WaitHandle.WaitOne(3000);
        }
    }

    private void ReadLoop(SerialPort port, CancellationToken ct)
    {
        var line = new StringBuilder();

        while (!ct.IsCancellationRequested)
        {
            if (port.BytesToRead == 0)
            {
                Thread.Sleep(20);
                continue;
            }

            foreach (char c in port.ReadExisting())
            {
                if (c is '\r' or '\n')
                {
                    if (line.Length > 0)
                    {
                        Emit(line.ToString());
                        line.Clear();
                    }
                }
                else if (line.Length < 256)
                {
                    line.Append(c);
                }
            }
        }
    }

    private void Emit(string text)
    {
        try { LineReceived?.Invoke(text); }
        catch (Exception ex) { AppLog.Error("Lỗi xử lý dòng từ cân", ex); }
    }

    private void SetConnected(bool connected, string message)
    {
        bool changed = _connected != connected;
        _connected = connected;

        if (changed || !connected)
            AppLog.Info("Cân: " + message);

        try { ConnectionChanged?.Invoke(connected, message); }
        catch (Exception ex) { AppLog.Error("Lỗi xử lý trạng thái cân", ex); }
    }

    public void Dispose() => Stop();
}
