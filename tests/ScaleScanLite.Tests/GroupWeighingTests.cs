using ScaleScanLite.Core.Logic;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Core.Workflow;
using Xunit;

namespace ScaleScanLite.Tests;

public class WeightSplitterTests
{
    [Fact]
    public void Splits_by_pcs()
        => Assert.Equal(new[] { 15.0, 10.0 }, WeightSplitter.Split(25, new double[] { 3, 2 }));

    [Fact]
    public void Rounding_remainder_goes_to_the_last_po_so_the_total_is_exact()
    {
        var parts = WeightSplitter.Split(10, new double[] { 1, 1, 1 });
        Assert.Equal(new[] { 3.333, 3.333, 3.334 }, parts);
        Assert.Equal(10.0, Math.Round(parts.Sum(), 3));
    }

    [Fact]
    public void Splits_evenly_when_pcs_are_missing()
        => Assert.Equal(new[] { 5.0, 5.0 }, WeightSplitter.Split(10, new double[] { 0, 0 }));

    [Fact]
    public void Empty_input_gives_empty_result()
        => Assert.Empty(WeightSplitter.Split(10, Array.Empty<double>()));
}

public class GroupWeighingTests
{
    private const string A = "521000000001";
    private const string B = "522000000002";
    private const string C = "523000000003";

    private readonly FakePoRepository _po = new();
    private readonly FakeHistory _history = new();
    private readonly FakeScale _scale = new();
    private readonly FakeErrorLog _errors = new();
    private readonly WeighingSettings _settings = new()
    {
        StableCount = 2, StableSpreadKg = 0.01, MinWeightKg = 0.1, ToleranceKg = 0.05, TolerancePercent = 0, GroupWindowSeconds = 5
    };
    private readonly List<WorkflowNotice> _notices = new();
    private readonly List<WeighingOutcome> _outcomes = new();
    private readonly List<IReadOnlyList<PoInfo>> _groups = new();
    private readonly WeighingWorkflow _workflow;
    private DateTime _now = new(2026, 11, 9, 8, 0, 0);

    public GroupWeighingTests()
    {
        // 5 kg mỗi pcs: A 3 pcs = 15 kg chuẩn, B 2 pcs = 10 kg chuẩn, C 5 pcs = 25 kg chuẩn
        Add(A, 3, "CUST1");
        Add(B, 2, "cust1 ");   // cùng custno, khác hoa thường và có khoảng trắng
        Add(C, 5, "CUST1");
        Add("524000000004", 2, "CUST2");
        Add("525000000005", 2, "");
        Add("526000000006", 2, "");

        _workflow = new WeighingWorkflow(_po, _history, _scale, _errors, new FakePending(), () => _settings, () => _now);
        _workflow.Notice += n => _notices.Add(n);
        _workflow.Completed += o => _outcomes.Add(o);
        _workflow.GroupFormed += (g, _) => _groups.Add(g);
    }

    private void Add(string aufnr, double pcs, string cust)
        => _po.Data[aufnr] = new PoInfo(aufnr, "P", "H", "L", pcs, 25, 5, cust);

    private async Task Weigh(double kg)
    {
        await _workflow.OnScaleReadingAsync(kg);
        await _workflow.OnScaleReadingAsync(kg);
    }

    [Fact]
    public async Task Two_pos_with_the_same_custno_scanned_within_the_window_share_one_weighing_split_by_pcs()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(3);
        await _workflow.HandleCodeAsync(B);

        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
        Assert.Equal(new[] { A, B }, _groups.Single().Select(p => p.Aufnr).ToArray());
        Assert.Contains(_notices, n => n.Message.Contains("Ghép 2 PO"));

        await Weigh(25);

        Assert.Equal(2, _history.Rows.Count);
        Assert.Equal(15, _history.Rows[A].Kg);
        Assert.Equal(10, _history.Rows[B].Kg);

        var outcome = Assert.Single(_outcomes);
        Assert.Equal(25, outcome.Kg);
        Assert.Equal(2, outcome.Group!.Count);
        Assert.True(outcome.Check!.WithinTolerance);
        Assert.Equal(25, outcome.Check.Expected);
        Assert.Null(_workflow.CurrentPo);                   // xong gói: xóa PO, chờ quét mới
        Assert.Empty(_errors.Items);
    }

    [Fact]
    public async Task Different_custno_within_the_window_keeps_only_the_last_po()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(2);
        await _workflow.HandleCodeAsync("524000000004");
        await Weigh(10);

        Assert.Empty(_groups);
        var saved = Assert.Single(_history.Saved);
        Assert.Equal("524000000004", saved.Aufnr);
        Assert.Equal(10, saved.Kg);
    }

    [Fact]
    public async Task Same_custno_after_the_window_is_not_grouped()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(6);
        await _workflow.HandleCodeAsync(B);
        await Weigh(10);

        Assert.Empty(_groups);
        Assert.Equal(B, _history.Saved.Single().Aufnr);
    }

    [Fact]
    public async Task Empty_custno_is_never_grouped()
    {
        await _workflow.HandleCodeAsync("525000000005");
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync("526000000006");
        await Weigh(10);

        Assert.Empty(_groups);
        Assert.Equal("526000000006", _history.Saved.Single().Aufnr);
    }

    [Fact]
    public async Task Window_of_zero_turns_grouping_off()
    {
        _settings.GroupWindowSeconds = 0;
        await _workflow.HandleCodeAsync(A);
        await _workflow.HandleCodeAsync(B);
        await Weigh(10);

        Assert.Empty(_groups);
        Assert.Equal(B, _history.Saved.Single().Aufnr);
    }

    [Fact]
    public async Task A_third_po_within_the_window_joins_the_group()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(2);
        await _workflow.HandleCodeAsync(B);
        _now = _now.AddSeconds(2);
        await _workflow.HandleCodeAsync(C);

        Assert.Equal(3, _groups.Last().Count);
        await Weigh(50);                                     // 3 + 2 + 5 = 10 pcs → 15 / 10 / 25

        Assert.Equal(new[] { 15.0, 10.0, 25.0 }, new[] { _history.Rows[A].Kg, _history.Rows[B].Kg, _history.Rows[C].Kg });
    }

    [Fact]
    public async Task Rescanning_a_po_of_the_group_changes_nothing()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(A);

        Assert.Single(_groups);
        Assert.Equal(2, _po.Calls);                          // quét lại A không tra lại
        await Weigh(25);
        Assert.Equal(2, _history.Rows.Count);
    }

    [Fact]
    public async Task Tolerance_compares_the_total_with_the_sum_of_expected_weights_and_logs_once()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);
        await Weigh(27);                                     // chuẩn 25, lệch 2 kg

        Assert.Single(_errors.Items);
        var warning = Assert.Single(_notices, n => n.Level == NoticeLevel.Warning);
        Assert.Contains("Lệch khối lượng gói", warning.Message);
        Assert.Equal(27, _history.Rows.Values.Sum(r => r.Kg), 3);
    }

    [Fact]
    public async Task Weighing_a_grouped_po_again_updates_its_rows_and_reports_previous_kg()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);
        await Weigh(25);
        _notices.Clear();

        _now = _now.AddSeconds(30);
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);
        await Weigh(25.05);

        Assert.Equal(2, _history.Rows.Count);
        Assert.Equal(SaveResult.Updated, _outcomes.Last().Result);
        Assert.Contains(_notices, n => n.Message.Contains("cập nhật cân mới") && n.Message.Contains("trước đó 15 kg"));
    }

    [Fact]
    public async Task Unknown_second_po_drops_the_first_so_the_weight_is_never_saved_for_the_wrong_po()
    {
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync("999999999999");
        await Weigh(25);

        Assert.Empty(_history.Saved);
        Assert.Null(_workflow.CurrentPo);
    }
}
