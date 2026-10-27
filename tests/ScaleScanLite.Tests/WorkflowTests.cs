using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Core.Workflow;

namespace ScaleScanLite.Tests;

internal sealed class FakePoRepository : IPoRepository
{
    public Dictionary<string, PoInfo> Data { get; } = new();
    public Dictionary<string, TaskCompletionSource<bool>> Hold { get; } = new();
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }

    public async Task<PoInfo?> FindAsync(string aufnr, CancellationToken ct)
    {
        Calls++;
        if (Hold.TryGetValue(aufnr, out var gate))
            await gate.Task;
        if (Throw != null) throw Throw;
        return Data.TryGetValue(aufnr, out var po) ? po : null;
    }
}

internal sealed class FakeHistory : IHistoryRepository
{
    /// <summary>Mỗi lần SaveAsync thành công (kể cả cập nhật).</summary>
    public List<WeighingRecord> Saved { get; } = new();

    /// <summary>Lịch sử hiện có: mỗi PO một dòng (dòng mới nhất).</summary>
    public Dictionary<string, WeighingRecord> Rows { get; } = new();

    public Exception? Throw { get; set; }

    /// <summary>Nếu đặt, SaveAsync sẽ chờ cổng này (mô phỏng lưu chậm).</summary>
    public TaskCompletionSource<bool>? Gate { get; set; }

    public async Task<SaveOutcome> SaveAsync(WeighingRecord record, CancellationToken ct)
    {
        if (Gate != null)
            await Gate.Task;
        if (Throw != null)
            throw Throw;

        Saved.Add(record);
        if (Rows.TryGetValue(record.Aufnr, out var old))
        {
            Rows[record.Aufnr] = record;
            return new SaveOutcome(SaveResult.Updated, old.Kg);
        }

        Rows[record.Aufnr] = record;
        return new SaveOutcome(SaveResult.Saved);
    }

    public Task<HistoryPage> QueryAsync(string? poFilter, bool contains, int page, int pageSize, CancellationToken ct)
        => Task.FromResult(new HistoryPage(Rows.Values.ToList(), Rows.Count));

    public Task<IReadOnlyList<WeighingRecord>> QueryAllAsync(string? poFilter, bool contains, int maxRows, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<WeighingRecord>>(Rows.Values.ToList());
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
    private const string Po2 = "520006700001";

    private readonly FakePoRepository _po = new();
    private readonly FakeHistory _history = new();
    private readonly FakeScale _scale = new();
    private readonly FakeErrorLog _errors = new();
    private readonly FakePending _pending = new();
    private readonly WeighingSettings _settings = new() { StableCount = 3, StableSpreadKg = 0.01, MinWeightKg = 0.1, ToleranceKg = 0.05, TolerancePercent = 0 };
    private readonly List<WorkflowNotice> _notices = new();
    private readonly List<WeighingOutcome> _outcomes = new();
    private readonly List<string> _loaded = new();
    private int _poCleared;
    private readonly WeighingWorkflow _workflow;

    public WorkflowTests()
    {
        // NW 30 / Qty 3 = 10 kg mỗi sản phẩm; số lượng 2 => chuẩn 20 kg
        _po.Data[Po] = new PoInfo(Po, "P1", "Hàng A", "Loại X", 2, 30, 3);
        _po.Data[Po2] = new PoInfo(Po2, "P2", "Hàng B", "Loại Y", 2, 30, 3);
        _workflow = new WeighingWorkflow(_po, _history, _scale, _errors, _pending, () => _settings,
            () => new DateTime(2026, 10, 7, 8, 0, 0));
        _workflow.Notice += n => _notices.Add(n);
        _workflow.Completed += o => _outcomes.Add(o);
        _workflow.PoLoaded += p => _loaded.Add(p.Aufnr);
        _workflow.PoCleared += () => _poCleared++;
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
    public async Task Po_is_cleared_after_weighing_and_the_scale_is_ignored_until_a_new_po_is_scanned()
    {
        await _workflow.HandleCodeAsync(Po);
        await Feed(20, 20, 20);

        Assert.Null(_workflow.CurrentPo);
        Assert.Equal(WorkflowState.Idle, _workflow.State);
        Assert.Equal(1, _poCleared);

        await Feed(20, 20, 20);            // số cân đọc thêm không được lưu cho PO nào
        Assert.Single(_history.Saved);

        await _workflow.HandleCodeAsync(Po2);
        Assert.Equal(Po2, _workflow.CurrentPo!.Aufnr);
    }

    [Fact]
    public async Task Latest_scanned_po_wins_when_the_first_scan_was_a_mistake()
    {
        await _workflow.HandleCodeAsync(Po);              // quét nhầm PO1
        await _workflow.HandleCodeAsync(Po2);             // quét lại đúng PO2 rồi mới bỏ lên cân
        Assert.Equal(Po2, _workflow.CurrentPo!.Aufnr);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);

        await Feed(20, 20, 20);

        var saved = Assert.Single(_history.Saved);
        Assert.Equal(Po2, saved.Aufnr);
        Assert.DoesNotContain(Po, _history.Rows.Keys);
        Assert.Equal(new[] { Po, Po2 }, _loaded);
        Assert.Equal(2, _poCleared);                      // một lần khi bỏ PO1, một lần sau khi cân xong PO2
        Assert.Null(_workflow.CurrentPo);
    }

    [Fact]
    public async Task Scanning_the_same_po_again_while_waiting_keeps_the_weighing_progress()
    {
        await _workflow.HandleCodeAsync(Po);
        await Feed(20, 20);                               // mới 2/3 lần đọc ổn định
        await _workflow.HandleCodeAsync(Po);              // quét lại đúng PO đang chờ

        Assert.Equal(1, _po.Calls);
        Assert.Equal(1, _scale.Starts);

        await Feed(20);
        Assert.Single(_history.Saved);
    }

    [Fact]
    public async Task A_code_that_is_not_a_po_does_not_cancel_the_po_being_weighed()
    {
        await _workflow.HandleCodeAsync(Po);
        await _workflow.HandleCodeAsync("abc");

        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
        Assert.Equal(Po, _workflow.CurrentPo!.Aufnr);
        Assert.Contains(_notices, n => n.Level == NoticeLevel.Warning);

        await Feed(20, 20, 20);
        Assert.Equal(Po, _history.Saved.Single().Aufnr);
    }

    [Fact]
    public async Task Scanning_an_unknown_po_cancels_the_previous_one_so_the_weight_is_never_saved_for_the_wrong_po()
    {
        await _workflow.HandleCodeAsync(Po);
        await _workflow.HandleCodeAsync("999999999999");

        Assert.Equal(WorkflowState.Idle, _workflow.State);
        Assert.Null(_workflow.CurrentPo);
        Assert.Contains(_notices, n => n.Message.Contains("Không tìm thấy"));

        await Feed(20, 20, 20);
        Assert.Empty(_history.Saved);
    }

    [Fact]
    public async Task Result_of_an_older_lookup_is_discarded_when_a_newer_po_was_scanned()
    {
        var gate = new TaskCompletionSource<bool>();
        _po.Hold[Po] = gate;

        var first = _workflow.HandleCodeAsync(Po);        // tra PO1 bị treo
        await _workflow.HandleCodeAsync(Po2);             // quét PO2 trong lúc đó
        Assert.Equal(Po2, _workflow.CurrentPo!.Aufnr);

        gate.SetResult(true);                             // PO1 trả kết quả muộn
        await first;

        Assert.Equal(Po2, _workflow.CurrentPo!.Aufnr);
        Assert.Equal(new[] { Po2 }, _loaded);
        Assert.Equal(1, _scale.Starts);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
    }

    [Fact]
    public async Task A_po_scanned_while_the_previous_one_is_being_saved_is_handled_right_after_the_save()
    {
        await _workflow.HandleCodeAsync(Po);
        _history.Gate = new TaskCompletionSource<bool>();  // lưu bị treo để giữ trạng thái Saving

        var weighing = Feed(20, 20, 20);
        for (int i = 0; i < 300 && _workflow.State != WorkflowState.Saving; i++)
            await Task.Delay(10);
        Assert.Equal(WorkflowState.Saving, _workflow.State);

        await _workflow.HandleCodeAsync(Po2);             // quét PO2 khi PO1 đang lưu: chưa tra ngay
        Assert.Equal(1, _po.Calls);

        _history.Gate.SetResult(true);
        await weighing;
        Assert.Equal(Po, _history.Saved.Single().Aufnr);
        Assert.Equal(Po2, _workflow.CurrentPo!.Aufnr);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
    }

    [Fact]
    public async Task Weighing_an_already_weighed_po_updates_the_kg_and_tells_the_user_the_new_and_old_values()
    {
        await _workflow.HandleCodeAsync(Po);
        await Feed(20.0, 20.0, 20.0);
        _notices.Clear();

        await _workflow.HandleCodeAsync(Po);              // cân lại cùng PO
        await Feed(20.03, 20.03, 20.03);

        Assert.Equal(2, _history.Saved.Count);
        Assert.Single(_history.Rows);                     // vẫn chỉ một dòng cho PO
        Assert.Equal(20.03, _history.Rows[Po].Kg);

        var outcome = _outcomes.Last();
        Assert.Equal(SaveResult.Updated, outcome.Result);
        Assert.Equal(20.0, outcome.PreviousKg);

        var notice = Assert.Single(_notices);
        Assert.Contains("cập nhật cân mới", notice.Message);
        Assert.Contains("20.03", notice.Message);
        Assert.Contains("trước đó 20 kg", notice.Message);
    }

    [Fact]
    public async Task An_updated_weight_outside_the_tolerance_still_warns_and_mentions_the_update()
    {
        _history.Rows[Po] = new WeighingRecord(1, Po, "P1", "Hàng A", "Loại X", 2, 20.0, new DateTime(2026, 10, 6));

        await _workflow.HandleCodeAsync(Po);
        await Feed(25, 25, 25);

        Assert.Single(_errors.Items);
        var notice = Assert.Single(_notices, n => n.Level == NoticeLevel.Warning);
        Assert.Contains("Lệch", notice.Message);
        Assert.Contains("đã cập nhật cân mới", notice.Message);
        Assert.Equal(25, _history.Rows[Po].Kg);
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
