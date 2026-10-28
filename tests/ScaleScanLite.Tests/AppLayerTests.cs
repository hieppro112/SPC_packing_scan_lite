using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Infrastructure;
using ScaleScanLite.Services;
using ScaleScanLite.Services.Data;
using ScaleScanLite.Services.Demo;
using ScaleScanLite.Services.Scale;
using ScaleScanLite.Views;

namespace ScaleScanLite.Tests;

public class SecretProtectorTests
{
    [Fact]
    public void Protect_roundtrips_and_is_not_plain_text()
    {
        var protectedText = SecretProtector.Protect("Mật-khẩu_123");

        Assert.NotEqual("Mật-khẩu_123", protectedText);
        Assert.DoesNotContain("123", protectedText);
        Assert.Equal("Mật-khẩu_123", SecretProtector.Unprotect(protectedText));
    }

    [Fact]
    public void Empty_and_corrupt_values_are_safe()
    {
        Assert.Equal("", SecretProtector.Protect(""));
        Assert.Equal("", SecretProtector.Unprotect(""));
        Assert.Equal("", SecretProtector.Unprotect("không-phải-base64!!"));
        Assert.Equal("", SecretProtector.Unprotect(Convert.ToBase64String(new byte[] { 1, 2, 3 })));
    }
}

public class SqlLikeEscapeTests
{
    [Theory]
    [InlineData("5200", "5200")]
    [InlineData("50%", "50\\%")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("[x]", "\\[x]")]
    [InlineData("a\\b", "a\\\\b")]
    public void Special_characters_are_escaped(string input, string expected)
        => Assert.Equal(expected, SqlHistoryRepository.EscapeLike(input));
}

public class SettingsFormTests
{
    [Fact]
    public void Defaults_roundtrip_without_errors()
    {
        var original = new AppSettings();
        var form = SettingsForm.From(original);
        var copy = new AppSettings();

        var errors = form.ApplyTo(copy);

        Assert.Empty(errors);
        Assert.Equal(original.Scale.PortName, copy.Scale.PortName);
        Assert.Equal(original.Weighing.ToleranceKg, copy.Weighing.ToleranceKg);
        Assert.Equal(original.Weighing.StableCount, copy.Weighing.StableCount);
        Assert.Equal(original.Scanner.MaxCharGapMs, copy.Scanner.MaxCharGapMs);
    }

    [Fact]
    public void Invalid_numbers_are_reported_with_field_names()
    {
        var form = SettingsForm.From(new AppSettings());
        form.BaudRate = "abc";
        form.ToleranceKg = "-1";
        form.StableCount = "500";
        form.Server = "  ";

        var errors = form.ApplyTo(new AppSettings());

        Assert.Equal(4, errors.Count);
        Assert.Contains(errors, e => e.Contains("Baud rate"));
        Assert.Contains(errors, e => e.Contains("Dung sai (kg)"));
        Assert.Contains(errors, e => e.Contains("Số lần đọc ổn định"));
        Assert.Contains(errors, e => e.Contains("Máy chủ SQL"));
    }

    [Fact]
    public void Decimal_comma_is_accepted()
    {
        var form = SettingsForm.From(new AppSettings());
        form.ToleranceKg = "0,25";
        var target = new AppSettings();

        Assert.Empty(form.ApplyTo(target));
        Assert.Equal(0.25, target.Weighing.ToleranceKg);
    }
}

public class PendingQueueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"pending-{Guid.NewGuid():N}.jsonl");
    private readonly TestHistory _history = new();

    private sealed class TestHistory : IHistoryRepository
    {
        public bool Offline { get; set; }
        public HashSet<string> Existing { get; } = new();
        public List<WeighingRecord> Saved { get; } = new();

        public Task<SaveOutcome> SaveAsync(WeighingRecord record, CancellationToken ct)
        {
            if (Offline) throw new DataUnavailableException("offline");
            Saved.Add(record);
            return Task.FromResult(Existing.Add(record.Aufnr)
                ? new SaveOutcome(SaveResult.Saved)
                : new SaveOutcome(SaveResult.Updated, 1.0));
        }

        public Task<HistoryPage> QueryAsync(string? f, bool c, int p, int s, CancellationToken ct)
            => Task.FromResult(new HistoryPage(Saved, Saved.Count));

        public Task<IReadOnlyList<WeighingRecord>> QueryAllAsync(string? f, bool c, int m, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<WeighingRecord>>(Saved);
    }

    private static WeighingRecord Rec(string po, double kg = 1.5)
        => new(0, po, "P", "H", "L", 2, kg, new DateTime(2026, 10, 7, 9, 0, 0));

    private PendingQueue Create() => new(_history, _path, TimeSpan.FromHours(1));

    [Fact]
    public async Task Offline_records_are_kept_then_synced_when_back_online()
    {
        using var queue = Create();
        queue.Enqueue(Rec("A1"));
        queue.Enqueue(Rec("A2"));
        Assert.Equal(2, queue.Count);

        _history.Offline = true;
        Assert.Equal(0, await queue.SyncAsync());
        Assert.Equal(2, queue.Count);

        _history.Offline = false;
        int synced = 0;
        queue.Synced += n => synced = n;
        Assert.Equal(2, await queue.SyncAsync());

        Assert.Equal(0, queue.Count);
        Assert.Equal(2, synced);
        Assert.Equal(new[] { "A1", "A2" }, _history.Saved.Select(r => r.Aufnr).ToArray());
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task Records_for_already_weighed_po_update_the_row_and_leave_the_queue()
    {
        using var queue = Create();
        _history.Existing.Add("DUP");
        queue.Enqueue(Rec("DUP", 2.0));
        queue.Enqueue(Rec("OK"));

        Assert.Equal(2, await queue.SyncAsync());
        Assert.Equal(0, queue.Count);
        Assert.Equal(2, _history.Saved.Count);
    }

    [Fact]
    public async Task Queue_survives_restart_and_preserves_values()
    {
        using (var first = Create())
            first.Enqueue(Rec("KEEP", 12.345));

        using var second = Create();
        Assert.Equal(1, second.Count);

        await second.SyncAsync();
        var saved = _history.Saved.Single();
        Assert.Equal("KEEP", saved.Aufnr);
        Assert.Equal(12.345, saved.Kg);
        Assert.Equal(new DateTime(2026, 10, 7, 9, 0, 0), saved.WeighedAt);
    }

    [Fact]
    public void Corrupt_lines_are_ignored()
    {
        File.WriteAllText(_path, "không phải json\n\n");
        using var queue = Create();
        Assert.Equal(0, queue.Count);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}

public class ScaleServiceTests
{
    private sealed class FakePort : IScalePort
    {
        public List<string> Sent { get; } = new();
        public bool IsConnected { get; set; } = true;
        public event Action<string>? LineReceived;
        public event Action<bool, string>? ConnectionChanged;
        public void Start(ScaleSettings settings) => ConnectionChanged?.Invoke(true, "ok");
        public void Stop() { }
        public bool Send(string command) { Sent.Add(command); return true; }
        public void Raise(string line) => LineReceived?.Invoke(line);
        public void Dispose() { }
    }

    private static SettingsService Settings(int poll = 100)
    {
        var s = new SettingsService();
        s.Current.Scale.PollIntervalMs = poll;
        s.Current.Scale.RequestCommand = "SP";
        return s;
    }

    [Fact]
    public async Task Sends_request_command_only_while_polling()
    {
        var port = new FakePort();
        using var service = new ScaleService(port, Settings(100));

        await Task.Delay(350);
        Assert.Empty(port.Sent);

        service.StartPolling();
        await Task.Delay(450);
        service.StopPolling();
        int count = port.Sent.Count;

        Assert.True(count >= 3, $"chỉ gửi {count} lệnh");
        Assert.All(port.Sent, c => Assert.Equal("SP", c));

        await Task.Delay(350);
        Assert.Equal(count, port.Sent.Count);
    }

    [Fact]
    public void Parses_scale_lines_into_kg_and_ignores_garbage()
    {
        var port = new FakePort();
        using var service = new ScaleService(port, Settings());
        var readings = new List<double>();
        service.ReadingReceived += readings.Add;

        port.Raise("ST,GS,   12.345 kg");
        port.Raise("ERR");
        port.Raise("  0.250");

        Assert.Equal(new[] { 12.345, 0.25 }, readings);
    }

    [Fact]
    public async Task Fake_scale_ramps_up_then_stabilises()
    {
        using var port = new FakeScalePort();
        var lines = new List<string>();
        port.LineReceived += l => { lock (lines) lines.Add(l); };
        port.Start(new ScaleSettings());

        for (int i = 0; i < 8; i++)
        {
            port.Send("SP");
            await Task.Delay(80);
        }

        List<double> values;
        lock (lines)
            values = lines.Select(l => ScaleScanLite.Core.Logic.ScaleLineParser.TryParse(l, out var kg) ? kg : double.NaN).ToList();

        Assert.Equal(8, values.Count);
        Assert.True(values[0] < values[3]);                       // đang tăng
        Assert.True(Math.Abs(values[6] - values[7]) < 0.01);      // rồi ổn định
    }
}

public class DemoAndExportTests
{
    [Fact]
    public async Task In_memory_history_inserts_updates_filters_and_pages()
    {
        var repo = new InMemoryHistoryRepository();
        var first = await repo.QueryAsync(null, false, 1, 10, CancellationToken.None);
        Assert.Equal(37, first.TotalItems);
        Assert.Equal(10, first.Items.Count);
        Assert.True(first.Items[0].Id > first.Items[1].Id);   // mới nhất trước

        var rec = new WeighingRecord(0, "9999999999", "P", "H", "L", 1, 2, DateTime.Now);
        Assert.Equal(new SaveOutcome(SaveResult.Saved), await repo.SaveAsync(rec, CancellationToken.None));
        var again = await repo.SaveAsync(rec with { Kg = 3.5 }, CancellationToken.None);
        Assert.Equal(SaveResult.Updated, again.Result);
        Assert.Equal(2, again.PreviousKg);

        var prefix = await repo.QueryAsync("99999", false, 1, 10, CancellationToken.None);
        Assert.Single(prefix.Items);
        var contains = await repo.QueryAsync("9999999", true, 1, 10, CancellationToken.None);
        Assert.Single(contains.Items);
        var last = await repo.QueryAsync(null, false, 4, 10, CancellationToken.None);
        Assert.Equal(8, last.Items.Count);
    }

    [Fact]
    public async Task Demo_po_repo_returns_data_except_for_zero_prefixed_codes()
    {
        var repo = new DemoPoRepository();
        var po = await repo.FindAsync("520006656409", CancellationToken.None);
        Assert.NotNull(po);
        Assert.Equal(DemoData.ExpectedKg,
            ScaleScanLite.Core.Logic.WeightCalculator.ExpectedKg(po!.NetWeight, po.PackQty, po.Quantity));
        Assert.Null(await repo.FindAsync("000012345678", CancellationToken.None));
    }

    [Fact]
    public void Excel_export_writes_all_rows_with_correct_values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.xlsx");
        try
        {
            var rows = new[]
            {
                new WeighingRecord(2, "PO2", "P2", "H2", "L2", 5, 12.5, new DateTime(2026, 10, 7, 10, 30, 0)),
                new WeighingRecord(1, "PO1", "P1", "H1", "L1", 3, 0.25, new DateTime(2026, 10, 6, 8, 0, 0))
            };

            ExcelExporter.Export(rows, path);

            using var wb = new ClosedXML.Excel.XLWorkbook(path);
            var ws = wb.Worksheet(1);
            Assert.Equal("Mã PO", ws.Cell(1, 2).GetString());
            Assert.Equal("PO2", ws.Cell(2, 2).GetString());
            Assert.Equal(12.5, ws.Cell(2, 7).GetDouble());
            Assert.Equal(new DateTime(2026, 10, 7, 10, 30, 0), ws.Cell(2, 8).GetDateTime());
            Assert.Equal("PO1", ws.Cell(3, 2).GetString());
            Assert.True(ws.Cell(4, 1).IsEmpty());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Error_log_writer_creates_json_with_expected_fields()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ssp-errlog-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new SettingsService();
            settings.Current.ErrorLogFolder = folder;
            var writer = new ErrorLogWriter(settings);
            var po = new PoInfo("PO/1", "P", "H", "L", 2, 30, 3);
            var record = new WeighingRecord(0, "PO/1", "P", "H", "L", 2, 25.0, new DateTime(2026, 10, 7, 8, 0, 0, 123));
            var check = new WeightCheckResult(20.0, 25.0, 5.0, 0.4, false);

            writer.WriteMismatch(record, po, check);

            var file = Directory.GetFiles(folder).Single();
            Assert.Contains("PO1", Path.GetFileName(file));
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
            Assert.Equal(25.0, doc.RootElement.GetProperty("Kg").GetDouble());
            Assert.Equal(20.0, doc.RootElement.GetProperty("Expected").GetDouble());
            Assert.Equal(5.0, doc.RootElement.GetProperty("Difference").GetDouble());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
