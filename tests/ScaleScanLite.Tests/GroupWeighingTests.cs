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
        StableCount = 2, StableSpreadKg = 0.01, MinWeightKg = 0.1, ToleranceKg = 0.05, TolerancePercent = 0, GroupWindowSeconds = 5, DeductPerPo = 0
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
    public async Task Edited_quantity_is_used_for_expected_weight_the_saved_row_and_the_notice()
    {
        await _workflow.HandleCodeAsync(A);              // master 3 pcs → chuẩn 15 kg
        Assert.True(_workflow.TrySetQuantity(A, 4, out var expected));
        Assert.Equal(20, expected);                      // 4 pcs × 5 kg

        await Weigh(20);

        var row = _history.Rows[A];
        Assert.Equal(4, row.Quantity);
        Assert.Equal(20, row.Kg);
        Assert.Empty(_errors.Items);                     // đúng chuẩn theo số lượng đã chỉnh
        var notice = Assert.Single(_notices, n => n.Level == NoticeLevel.Success);
        Assert.Contains("Số lượng đã chỉnh", notice.Message);
        Assert.Contains("3→4 pcs", notice.Message);
    }

    [Fact]
    public async Task Without_editing_the_master_quantity_is_kept_and_no_note_is_shown()
    {
        await _workflow.HandleCodeAsync(A);
        await Weigh(15);

        Assert.Equal(3, _history.Rows[A].Quantity);
        Assert.DoesNotContain(_notices, n => n.Message.Contains("đã chỉnh"));
    }

    [Fact]
    public async Task Edited_quantities_drive_the_split_of_a_group()
    {
        await _workflow.HandleCodeAsync(A);              // 3 pcs
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);              // 2 pcs
        Assert.True(_workflow.TrySetQuantity(B, 3, out var expected));
        Assert.Equal(30, expected);                      // (3 + 3) × 5

        await Weigh(30);

        Assert.Equal(15, _history.Rows[A].Kg);
        Assert.Equal(15, _history.Rows[B].Kg);
        Assert.Equal(3, _history.Rows[B].Quantity);
        Assert.Contains(_notices, n => n.Message.Contains("2→3 pcs"));
    }

    [Fact]
    public async Task Quantity_edited_before_the_second_scan_is_kept_when_the_group_forms()
    {
        await _workflow.HandleCodeAsync(A);
        Assert.True(_workflow.TrySetQuantity(A, 1, out _));
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);              // B 2 pcs: tổng 3 pcs

        await Weigh(15);

        Assert.Equal(5, _history.Rows[A].Kg);            // 15 × 1/3
        Assert.Equal(10, _history.Rows[B].Kg);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task Invalid_quantities_are_rejected_and_the_previous_value_stays(double bad)
    {
        await _workflow.HandleCodeAsync(A);
        Assert.False(_workflow.TrySetQuantity(A, bad, out _));
        Assert.Equal(3, _workflow.CurrentPo!.Quantity);
    }

    [Fact]
    public async Task Quantity_cannot_be_changed_for_unknown_po_when_idle_or_while_saving()
    {
        Assert.False(_workflow.TrySetQuantity(A, 4, out _));          // chưa có PO

        await _workflow.HandleCodeAsync(A);
        Assert.False(_workflow.TrySetQuantity(B, 4, out _));          // PO không thuộc gói đang chờ

        _history.Gate = new TaskCompletionSource<bool>();
        var weighing = Weigh(15);
        for (int i = 0; i < 300 && _workflow.State != WorkflowState.Saving; i++)
            await Task.Delay(10);
        Assert.False(_workflow.TrySetQuantity(A, 4, out _));          // đang lưu: không đổi nữa

        _history.Gate.SetResult(true);
        await weighing;
        Assert.Equal(3, _history.Rows[A].Quantity);
    }

    [Fact]
    public void Deduction_per_po_defaults_to_5_like_the_old_hard_coded_value()
        => Assert.Equal(5, new WeighingSettings().DeductPerPo);

    [Fact]
    public async Task A_single_po_saves_the_weight_minus_one_deduction()
    {
        _settings.DeductPerPo = 1;
        await _workflow.HandleCodeAsync(A);              // chuẩn 15
        await Weigh(15);

        Assert.Equal(14, _history.Rows[A].Kg);           // lưu 15 − 1
        Assert.Empty(_errors.Items);                     // dung sai so với số cân thô (15 = chuẩn 15)
    }

    [Fact]
    public async Task A_group_deducts_per_po_times_the_number_of_sheets_before_splitting_by_pcs()
    {
        _settings.DeductPerPo = 1;
        await _workflow.HandleCodeAsync(A);              // 3 pcs
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);              // 2 pcs
        await Weigh(27);                                 // 27 − 1×2 = 25 → 15 / 10

        Assert.Equal(15, _history.Rows[A].Kg);
        Assert.Equal(10, _history.Rows[B].Kg);
        Assert.Contains(_notices, n => n.Message.Contains("Đã trừ 2 (1 × 2 PO)"));
    }

    [Fact]
    public async Task A_group_of_three_deducts_three_times()
    {
        _settings.DeductPerPo = 5;
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(C);
        await Weigh(65);                                 // 65 − 5×3 = 50 → 15 / 10 / 25

        Assert.Equal(new[] { 15.0, 10.0, 25.0 }, new[] { _history.Rows[A].Kg, _history.Rows[B].Kg, _history.Rows[C].Kg });
    }

    [Fact]
    public async Task Tolerance_still_uses_the_raw_scale_weight_not_the_deducted_one()
    {
        _settings.DeductPerPo = 1;
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);
        await Weigh(25);                                 // thô 25 = chuẩn 25 → đạt; số lưu 23

        Assert.Empty(_errors.Items);
        Assert.Equal(23, _history.Rows.Values.Sum(r => r.Kg), 3);
    }

    [Fact]
    public async Task A_weight_not_greater_than_the_deduction_is_rejected_and_the_po_keeps_waiting()
    {
        _settings.DeductPerPo = 5;
        await _workflow.HandleCodeAsync(A);
        await Weigh(5);                                  // 5 − 5 = 0: không lưu

        Assert.Empty(_history.Saved);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
        var warning = Assert.Single(_notices, n => n.Level == NoticeLevel.Warning);
        Assert.Contains("không lớn hơn phần trừ", warning.Message);

        await Weigh(20);                                 // đặt hàng đúng: lưu 15
        Assert.Equal(15, _history.Rows[A].Kg);
    }

    [Fact]
    public async Task A_group_weight_not_greater_than_the_total_deduction_is_rejected()
    {
        _settings.DeductPerPo = 5;
        await _workflow.HandleCodeAsync(A);
        _now = _now.AddSeconds(1);
        await _workflow.HandleCodeAsync(B);
        await Weigh(10);                                 // 10 − 5×2 = 0: không lưu

        Assert.Empty(_history.Saved);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
        Assert.Contains(_notices, n => n.Message.Contains("5 × 2 PO"));
    }

    [Fact]
    public async Task Zero_or_negative_readings_are_never_saved_even_when_the_minimum_is_zero()
    {
        _settings.MinWeightKg = 0;
        _settings.DeductPerPo = 0;
        await _workflow.HandleCodeAsync(A);
        await Weigh(0);
        await Weigh(-1);

        Assert.Empty(_history.Saved);
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);
    }

    [Fact]
    public async Task Confirm_button_refuses_zero_and_a_weight_below_the_deduction()
    {
        _settings.MinWeightKg = 0;
        _settings.DeductPerPo = 5;
        await _workflow.HandleCodeAsync(A);

        await _workflow.OnScaleReadingAsync(0);
        await _workflow.ConfirmAsync();
        Assert.Empty(_history.Saved);

        await _workflow.OnScaleReadingAsync(3);
        await _workflow.ConfirmAsync();
        Assert.Empty(_history.Saved);
        Assert.Contains(_notices, n => n.Message.Contains("không lớn hơn phần trừ"));
        Assert.Equal(WorkflowState.WaitingForWeight, _workflow.State);

        await _workflow.OnScaleReadingAsync(20);
        await _workflow.ConfirmAsync();
        Assert.Equal(15, _history.Rows[A].Kg);
    }

    [Fact]
    public void Stability_detector_never_accepts_zero_or_negative_values()
    {
        var detector = new ScaleScanLite.Core.Logic.StabilityDetector(2, 0.01, 0);
        Assert.False(detector.Add(0, out _));
        Assert.False(detector.Add(0, out _));
        Assert.False(detector.Add(-2, out _));
        Assert.False(detector.Add(-2, out _));
        Assert.False(detector.Add(1, out _));
        Assert.True(detector.Add(1, out var value));
        Assert.Equal(1, value);
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
