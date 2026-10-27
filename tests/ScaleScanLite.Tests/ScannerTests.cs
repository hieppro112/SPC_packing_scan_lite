using ScaleScanLite.Core.Logic;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Core.Workflow;
using ScaleScanLite.Services;
using ScaleScanLite.Services.Input;
using ScaleScanLite.Views;

namespace ScaleScanLite.Tests;

public class ScanBurstAssemblerTests
{
    private static string? Feed(ScanBurstAssembler a, string text, long start, int gap)
    {
        string? last = null;
        long t = start;
        foreach (char c in text)
        {
            last = a.Add(c, t);
            t += gap;
        }

        return last;
    }

    [Fact]
    public void Fast_burst_ending_with_enter_returns_the_code()
    {
        var a = new ScanBurstAssembler(100, 4);
        Assert.Equal("A,520006656409,XYZ", Feed(a, "A,520006656409,XYZ\r", 1000, 5));
    }

    [Fact]
    public void Slow_human_typing_never_forms_a_code()
    {
        var a = new ScanBurstAssembler(100, 4);
        Assert.Null(Feed(a, "12345678\r", 0, 400));
    }

    [Fact]
    public void Gap_inside_a_burst_discards_the_earlier_characters()
    {
        var a = new ScanBurstAssembler(100, 4);
        long t = 0;
        foreach (char c in "9999") a.Add(c, t += 5);
        t += 1000; // dừng lâu: phần trước bị bỏ
        foreach (char c in "52000665") a.Add(c, t += 5);

        Assert.Equal("52000665", a.Add('\r', t + 5));
    }

    [Fact]
    public void Too_short_codes_are_rejected_and_buffer_is_cleared()
    {
        var a = new ScanBurstAssembler(100, 4);
        Assert.Null(Feed(a, "12\r", 0, 5));
        Assert.Equal("123456", Feed(a, "123456\n", 100, 5));
    }

    [Fact]
    public void Tab_terminates_and_control_characters_are_ignored()
    {
        var a = new ScanBurstAssembler(100, 4);
        a.Add('\u0001', 1);
        Assert.Equal("ABCD", Feed(a, "ABCD\t", 10, 5));
    }

    [Fact]
    public void Reset_clears_partial_input()
    {
        var a = new ScanBurstAssembler(100, 4);
        Feed(a, "ABCD", 0, 5);
        a.Reset();
        Assert.Null(a.Add('\r', 30));
    }
}

public class ScannerDeviceIdTests
{
    private const string UsbPath = @"\\?\HID#VID_05E0&PID_1200&MI_00#7&2a3b4c5d&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
    private const string SamePortChanged = @"\\?\HID#VID_05E0&PID_1200&MI_00#8&99887766&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";
    private const string Keyboard = @"\\?\HID#VID_046D&PID_C31C#7&1111&0&0000#{884b96c3-56ef-11d1-bc8c-00a0c91405dd}";

    [Fact]
    public void Extracts_vid_pid_in_a_stable_form()
    {
        Assert.Equal("VID_05E0&PID_1200", ScannerDeviceId.FromDevicePath(UsbPath));
        Assert.Equal("VID_05E0&PID_1200", ScannerDeviceId.FromDevicePath(@"\\?\hid#vid_05e0&pid_1200&mi_00#abc"));
        Assert.Equal("", ScannerDeviceId.FromDevicePath(null));
    }

    [Fact]
    public void Same_scanner_matches_even_after_changing_usb_port_but_other_keyboard_does_not()
    {
        var id = ScannerDeviceId.FromDevicePath(UsbPath);
        Assert.True(ScannerDeviceId.Matches(id, UsbPath));
        Assert.True(ScannerDeviceId.Matches(id, SamePortChanged));
        Assert.False(ScannerDeviceId.Matches(id, Keyboard));
        Assert.False(ScannerDeviceId.Matches("", UsbPath));
    }

    [Fact]
    public void Unrecognised_paths_fall_back_to_the_whole_path()
    {
        var odd = @"\\?\ROOT#SOMETHING#1";
        Assert.Equal(odd.ToUpperInvariant(), ScannerDeviceId.FromDevicePath(odd));
        Assert.True(ScannerDeviceId.Matches(odd, odd));
    }
}

public class RawInputScannerTests
{
    private const string ScannerPath = @"\\?\HID#VID_05E0&PID_1200&MI_00#7&aaa&0&0000#{884b96c3}";
    private const string KeyboardPath = @"\\?\HID#VID_046D&PID_C31C#7&bbb&0&0000#{884b96c3}";
    private static readonly IntPtr ScannerHandle = new(101);
    private static readonly IntPtr KeyboardHandle = new(202);

    private const ushort VkShift = 0x10;
    private const ushort VkReturn = 0x0D;

    // Bộ dịch phím giả, không phụ thuộc bàn phím của máy chạy test.
    private static char? FakeTranslate(ushort vk, ushort scan, bool shift)
    {
        if (vk == VkReturn) return '\r';
        if (vk >= 0x30 && vk <= 0x39) return (char)vk;                 // 0-9
        if (vk >= 0x41 && vk <= 0x5A) return shift ? (char)vk : char.ToLower((char)vk);
        if (vk == 0xBC) return ',';
        return null;
    }

    private static ScannerSettings Config(bool enabled = true, string id = "VID_05E0&PID_1200")
        => new() { Enabled = enabled, DeviceId = id, MaxCharGapMs = 100, MinLength = 4 };

    private static void Type(RawInputScanner s, IntPtr dev, string path, string text, ref long t, int gap = 5)
    {
        foreach (char c in text)
        {
            ushort vk = c switch
            {
                '\r' => VkReturn,
                ',' => 0xBC,
                _ => char.ToUpper(c)
            };

            bool upper = char.IsUpper(c);
            if (upper) s.ProcessKey(dev, path, VkShift, 0, false, t += gap);
            s.ProcessKey(dev, path, vk, 0, false, t += gap);
            s.ProcessKey(dev, path, vk, 0, true, t += gap);
            if (upper) s.ProcessKey(dev, path, VkShift, 0, true, t += gap);
        }
    }

    [Fact]
    public void Configured_scanner_codes_are_emitted_including_shifted_letters_and_commas()
    {
        var scanner = new RawInputScanner(FakeTranslate);
        scanner.Configure(Config());
        var codes = new List<(string Code, string Id)>();
        scanner.CodeScanned += (c, id) => codes.Add((c, id));

        long t = 0;
        Type(scanner, ScannerHandle, ScannerPath, "A,520006656409,Xy\r", ref t);

        Assert.Equal(("A,520006656409,Xy", "VID_05E0&PID_1200"), codes.Single());
    }

    [Fact]
    public void Other_keyboards_are_ignored_completely()
    {
        var scanner = new RawInputScanner(FakeTranslate);
        scanner.Configure(Config());
        int count = 0;
        scanner.CodeScanned += (_, _) => count++;

        long t = 0;
        Type(scanner, KeyboardHandle, KeyboardPath, "520006656409\r", ref t);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Disabled_or_unconfigured_scanner_emits_nothing()
    {
        var scanner = new RawInputScanner(FakeTranslate);
        int count = 0;
        scanner.CodeScanned += (_, _) => count++;

        long t = 0;
        scanner.Configure(Config(enabled: false));
        Type(scanner, ScannerHandle, ScannerPath, "520006656409\r", ref t);
        scanner.Configure(Config(id: ""));
        Type(scanner, ScannerHandle, ScannerPath, "520006656409\r", ref t);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Slow_typing_from_the_scanner_device_is_not_treated_as_a_scan()
    {
        var scanner = new RawInputScanner(FakeTranslate);
        scanner.Configure(Config());
        int count = 0;
        scanner.CodeScanned += (_, _) => count++;

        long t = 0;
        Type(scanner, ScannerHandle, ScannerPath, "520006656409\r", ref t, gap: 300);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Two_consecutive_scans_produce_two_codes()
    {
        var scanner = new RawInputScanner(FakeTranslate);
        scanner.Configure(Config());
        var codes = new List<string>();
        scanner.CodeScanned += (c, _) => codes.Add(c);

        long t = 0;
        Type(scanner, ScannerHandle, ScannerPath, "520006656409\r", ref t);
        t += 2000;
        Type(scanner, ScannerHandle, ScannerPath, "520006700001\r", ref t);

        Assert.Equal(new[] { "520006656409", "520006700001" }, codes);
    }

    [Fact]
    public void Learning_picks_the_first_scan_from_any_device_then_stops_without_emitting_a_scan()
    {
        var scanner = new RawInputScanner(FakeTranslate);
        scanner.Configure(Config(enabled: false, id: ""));
        var learned = new List<(string Id, string Code)>();
        int scans = 0;
        scanner.DeviceLearned += (id, code) => learned.Add((id, code));
        scanner.CodeScanned += (_, _) => scans++;

        scanner.StartLearning();
        Assert.True(scanner.IsLearning);

        long t = 0;
        Type(scanner, ScannerHandle, ScannerPath, "520006656409\r", ref t);

        Assert.Equal(("VID_05E0&PID_1200", "520006656409"), learned.Single());
        Assert.False(scanner.IsLearning);
        Assert.Equal(0, scans);

        // sau khi nhận diện xong, thiết bị chưa được bật nên các lượt quét sau bị bỏ qua
        Type(scanner, ScannerHandle, ScannerPath, "520006700001\r", ref t);
        Assert.Single(learned);
        Assert.Equal(0, scans);
    }

    [Fact]
    public void Registration_with_windows_follows_the_configuration()
    {
        bool registeredWhileEnabled = false, registeredAfterDisable = true, registeredWhileLearning = false;

        var thread = new Thread(() =>
        {
            using var scanner = new RawInputScanner(FakeTranslate);
            scanner.Start();
            Assert.False(scanner.IsRegistered);              // chưa bật: không nghe phím nào

            scanner.Configure(Config());
            registeredWhileEnabled = scanner.IsRegistered;

            scanner.Configure(Config(enabled: false));
            registeredAfterDisable = scanner.IsRegistered;

            scanner.StartLearning();
            registeredWhileLearning = scanner.IsRegistered;
            scanner.StopLearning();
            Assert.False(scanner.IsRegistered);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(10000));

        Assert.True(registeredWhileEnabled);
        Assert.False(registeredAfterDisable);
        Assert.True(registeredWhileLearning);
    }
}

public class ScannerSettingsTests
{
    [Fact]
    public void Defaults_roundtrip_and_validation_requires_a_device_when_enabled()
    {
        var form = SettingsForm.From(new AppSettings());
        var target = new AppSettings();
        Assert.Empty(form.ApplyTo(target));
        Assert.False(target.Scanner.Enabled);
        Assert.Equal(100, target.Scanner.MaxCharGapMs);

        form.ScannerEnabled = true;
        form.ScannerDeviceId = "";
        var errors = form.ApplyTo(new AppSettings());
        Assert.Contains(errors, e => e.Contains("Máy quét USB"));

        form.ScannerDeviceId = "VID_05E0&PID_1200";
        form.ScannerMaxCharGapMs = "5";
        errors = form.ApplyTo(new AppSettings());
        Assert.Contains(errors, e => e.Contains("Khoảng cách ký tự"));
    }
}

public class DemoCodeFeederTests
{
    [Fact]
    public async Task Feeder_emits_sample_codes_in_order_and_wraps_around()
    {
        using var feeder = new ScaleScanLite.Services.Demo.DemoCodeFeeder(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));
        var codes = new List<string>();
        feeder.CodeScanned += c => { lock (codes) codes.Add(c); };

        feeder.Start();
        await Task.Delay(400);

        lock (codes)
        {
            var samples = ScaleScanLite.Services.Demo.DemoData.Codes;
            Assert.True(codes.Count > samples.Length, $"chỉ phát {codes.Count} mã");
            for (int i = 0; i < codes.Count; i++)
                Assert.Equal(samples[i % samples.Length], codes[i]);
        }

        // Mã mẫu phải đi qua đúng quy tắc PO (nếu không thì bản chạy thử sẽ báo "PO không hợp lệ")
        foreach (var code in ScaleScanLite.Services.Demo.DemoData.Codes)
            Assert.True(PoParser.TryParse(code, 11, out _), code);
    }
}

public class WorkflowForceTests
{
    private const string Po = "520006656409";

    [Fact]
    public async Task Force_allows_rescanning_the_same_code_after_a_weighing_without_reset()
    {
        var poRepo = new FakePoRepository();
        poRepo.Data[Po] = new PoInfo(Po, "P", "H", "L", 2, 30, 3);
        var history = new FakeHistory();
        var workflow = new WeighingWorkflow(poRepo, history, new FakeScale(), new FakeErrorLog(), new FakePending(),
            () => new WeighingSettings { StableCount = 2, StableSpreadKg = 0.01, MinWeightKg = 0.1 });

        await workflow.HandleCodeAsync(Po);
        await workflow.OnScaleReadingAsync(20);
        await workflow.OnScaleReadingAsync(20);
        Assert.Equal(1, poRepo.Calls);

        await workflow.HandleCodeAsync(Po);               // camera: bỏ qua vì trùng mã vừa xử lý
        Assert.Equal(1, poRepo.Calls);

        await workflow.HandleCodeAsync(Po, force: true);  // máy quét: thao tác cố ý nên được xử lý
        Assert.Equal(2, poRepo.Calls);
        Assert.Equal(WorkflowState.WaitingForWeight, workflow.State);
    }

    [Fact]
    public async Task Force_does_not_interrupt_a_weighing_in_progress()
    {
        var poRepo = new FakePoRepository();
        poRepo.Data[Po] = new PoInfo(Po, "P", "H", "L", 2, 30, 3);
        var workflow = new WeighingWorkflow(poRepo, new FakeHistory(), new FakeScale(), new FakeErrorLog(), new FakePending(),
            () => new WeighingSettings());

        await workflow.HandleCodeAsync(Po);
        Assert.Equal(WorkflowState.WaitingForWeight, workflow.State);

        await workflow.HandleCodeAsync("999999999999", force: true);
        Assert.Equal(1, poRepo.Calls);
        Assert.Equal(Po, workflow.CurrentPo!.Aufnr);
    }
}
