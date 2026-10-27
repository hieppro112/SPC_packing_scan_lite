using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Core.Workflow;

namespace ScaleScanLite.Tests;

internal sealed class FakePoRepository : IPoRepository
{
    public Dictionary<string, PoInfo> Data { get; } = new();
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }

    public Task<PoInfo?> FindAsync(string aufnr, CancellationToken ct)
    {
        Calls++;
        if (Throw != null) throw Throw;
        return Task.FromResult(Data.TryGetValue(aufnr, out var po) ? po : null);
    }
}

internal sealed class FakeHistory : IHistoryRepository
{
    public List<WeighingRecord> Saved { get; } = new();
    public Exception? Throw { get; set; }
    public HashSet<string> Existing { get; } = new();

    public Task<SaveResult> InsertAsync(WeighingRecord record, CancellationToken ct)
    {
        if (Throw != null) throw Throw;
        if (!Existing.Add(record.Aufnr)) return Task.FromResult(SaveResult.Duplicate);
        Saved.Add(record);
        return Task.FromResult(SaveResult.Saved);
    }

    public Task<HistoryPage> QueryAsync(string? poFilter, bool contains, int page, int pageSize, CancellationToken ct)
        => Task.FromResult(new HistoryPage(Saved, Saved.Count));

    public Task<IReadOnlyList<WeighingRecord>> QueryAllAsync(string? poFilter, bool contains, int maxRows, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<WeighingRecord>>(Saved);
}

internal sealed class FakeScale : IScaleReader
{
    public int Starts { get; private set; }
    public int Stops { get; private set; }
    public void StartPolling() => Starts++;
    public void StopPolling() => Stops++;
}

internal sealed class FakeErrorLog : IErrorLogWriter
{
    public List<(WeighingRecord Record, WeightCheckResult Check)> Items { get; } = new();
    public void WriteMismatch(WeighingRecord record, PoInfo po, WeightCheckResult check) => Items.Add((record, check));
}

internal sealed class FakePending : IPendingQueue
{
    public List<WeighingRecord> Items { get; } = new();
    public void Enqueue(WeighingRecord record) => Items.Add(record);
}

public class WorkflowTests
{
    private const string Po = "520006656409";

    private readonly FakePoRepository _po = new();
    private readonly FakeHistory _history = new();
    private readonly FakeScale _scale = new();
    private readonly FakeErrorLog _errors = new();
    private readonly FakePending _pending = new();
    private readonly WeighingSettings _settings = new() { StableCount = 3, StableSpreadKg = 0.01, MinWeightKg = 0.1, ToleranceKg = 0.05, TolerancePercent = 0 };
    private readonly List<WorkflowNotice> _notices = new();
    private readonly List<WeighingOutcome> _outcomes = new();
    private readonly WeighingWorkflow _workflow;

    public WorkflowTests()
    {
        // NW 30 / Qty 3 = 10 kg mỗi sản phẩm; số lượng 2 => chuẩn 20 kg
        _po.Data[Po] = new PoInfo(Po, "P1", "Hàng A", "Loại X", 2, 30, 3);
        _workflow = new WeighingWorkflow(_po, _history, _scale, _errors, _pending, () => _settings,
            () => new DateTime(2026, 10, 7, 8, 0, 0));
        _workflow.Notice += n => _notices.Add(n);
        _workflow.Completed += o => _outcomes.Add(o);
    }

    private async Task Feed(params double[] readings)
    {
        foreach (var r in readings)
            await _workflow.OnScaleReadingAsync(r);
    }

    [Fact]
    public async Task Happy_path_saves_once_when_weight_is_stable()
    {
        await _workflow.HandleCodeAsync(Po);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
        Assert.Equal(1, _scale.Starts);

        await Feed(5, 15, 20.0, 20.004, 20.002);

        Assert.Single(_history.Saved);
        Assert.Equal(20.002, _history.Saved[0].Kg);
        Assert.Equal(new DateTime(2026, 10, 7, 8, 0, 0), _history.Saved[0].WeighedAt);
        Assert.Equal(WorkflowState.Idle, _workflow.State);
        Assert.Contains(_notices, n => n.Level == NoticeLevel.Success);
        Assert.Empty(_errors.Items);
        Assert.True(_scale.Stops >= 1);

        // đọc thêm sau khi đã lưu không được lưu lần nữa
        await Feed(20.0, 20.0, 20.0);
        Assert.Single(_history.Saved);
    }

    [Fact]
    public async Task Mismatch_is_saved_but_logged_and_warned()
    {
        await _workflow.HandleCodeAsync(Po);
        await Feed(25.0, 25.0, 25.0);

        Assert.Single(_history.Saved);
        Assert.Single(_errors.Items);
        Assert.False(_errors.Items[0].Check.WithinTolerance);
        Assert.Contains(_notices, n => n.Level == NoticeLevel.Warning && n.Message.Contains("Lệch"));
        Assert.Equal(SaveResult.Saved, _outcomes.Single().Result);
    }

    [Fact]
    public async Task Same_code_is_ignored_until_reset()
    {
        await _workflow.HandleCodeAsync(Po);
        await Feed(20, 20, 20);
        Assert.Equal(1, _po.Calls);

        await _workflow.HandleCodeAsync(Po);
        Assert.Equal(1, _po.Calls);

        _workflow.Reset();
        await _workflow.HandleCodeAsync(Po);
        Assert.Equal(2, _po.Calls);
    }

    [Fact]
    public async Task Invalid_code_warns_and_stays_idle()
    {
        await _workflow.HandleCodeAsync("abc");

        Assert.Equal(WorkflowState.Idle, _workflow.State);
        Assert.Contains(_notices, n => n.Level == NoticeLevel.Warning);
        Assert.Equal(0, _po.Calls);
    }

    [Fact]
    public async Task Unknown_po_warns_and_stays_idle()
    {
        await _workflow.HandleCodeAsync("999999999999");

        Assert.Equal(WorkflowState.Idle, _workflow.State);
        Assert.Contains(_notices, n => n.Message.Contains("Không tìm thấy"));
        Assert.Equal(0, _scale.Starts);
    }

    [Fact]
    public async Task Po_lookup_failure_allows_rescan_of_same_code()
    {
        _po.Throw = new InvalidOperationException("db down");
        await _workflow.HandleCodeAsync(Po);
        Assert.Contains(_notices, n => n.Level == NoticeLevel.Error);

        _po.Throw = null;
        await _workflow.HandleCodeAsync(Po);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
    }

    [Fact]
    public async Task Duplicate_po_is_reported_and_not_saved_again()
    {
        _history.Existing.Add(Po);
        await _workflow.HandleCodeAsync(Po);
        await Feed(20, 20, 20);

        Assert.Empty(_history.Saved);
        Assert.Equal(SaveResult.Duplicate, _outcomes.Single().Result);
        Assert.Contains(_notices, n => n.Message.Contains("đã được cân"));
    }

    [Fact]
    public async Task Lost_connection_queues_record_locally()
    {
        _history.Throw = new DataUnavailableException("no network");
        await _workflow.HandleCodeAsync(Po);
        await Feed(20, 20, 20);

        Assert.Single(_pending.Items);
        Assert.Equal(20.0, _pending.Items[0].Kg);
        Assert.Equal(SaveResult.Queued, _outcomes.Single().Result);
        Assert.Equal(WorkflowState.Idle, _workflow.State);
    }

    [Fact]
    public async Task Unexpected_save_error_returns_to_idle_and_allows_rescan()
    {
        _history.Throw = new InvalidOperationException("boom");
        await _workflow.HandleCodeAsync(Po);
        await Feed(20, 20, 20);

        Assert.Empty(_outcomes);
        Assert.Equal(WorkflowState.Idle, _workflow.State);
        Assert.Contains(_notices, n => n.Level == NoticeLevel.Error);

        _history.Throw = null;
        await _workflow.HandleCodeAsync(Po);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
    }

    [Fact]
    public async Task Confirm_saves_latest_reading_without_waiting_for_stability()
    {
        await _workflow.HandleCodeAsync(Po);
        await Feed(19.0, 20.0);
        await _workflow.ConfirmAsync();

        Assert.Single(_history.Saved);
        Assert.Equal(20.0, _history.Saved[0].Kg);
    }

    [Fact]
    public async Task Confirm_without_reading_or_po_only_notifies()
    {
        await _workflow.ConfirmAsync();
        Assert.Empty(_history.Saved);
        Assert.Contains(_notices, n => n.Level == NoticeLevel.Info);

        await _workflow.HandleCodeAsync(Po);
        await _workflow.ConfirmAsync();
        Assert.Empty(_history.Saved);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
    }

    [Fact]
    public async Task Readings_are_ignored_when_not_waiting()
    {
        await Feed(20, 20, 20);
        Assert.Empty(_history.Saved);
    }

    [Fact]
    public async Task Reset_cancels_waiting_and_clears_po()
    {
        var cleared = 0;
        _workflow.Cleared += () => cleared++;

        await _workflow.HandleCodeAsync(Po);
        _workflow.Reset();

        Assert.Equal(WorkflowState.Idle, _workflow.State);
        Assert.Null(_workflow.CurrentPo);
        Assert.Equal(1, cleared);

        await Feed(20, 20, 20);
        Assert.Empty(_history.Saved);
    }

    [Fact]
    public async Task Missing_pack_data_saves_without_comparison()
    {
        _po.Data["111111111111"] = new PoInfo("111111111111", "P", "H", "L", 5, 0, 0);
        await _workflow.HandleCodeAsync("111111111111");
        await Feed(3, 3, 3);

        Assert.Single(_history.Saved);
        Assert.Null(_outcomes.Single().Check);
        Assert.Empty(_errors.Items);
    }
}
