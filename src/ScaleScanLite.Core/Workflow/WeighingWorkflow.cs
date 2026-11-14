using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Logic;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;

namespace ScaleScanLite.Core.Workflow;

public enum WorkflowState
{
    Idle,
    LookingUpPo,
    WaitingForWeight,
    Saving
}

public enum NoticeLevel
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record WorkflowNotice(NoticeLevel Level, string Message);

/// <summary>
/// Luồng nghiệp vụ: quét PO → tra PO → chờ số cân ổn định → lưu → so khối lượng chuẩn.
///
/// Quy tắc:
/// - PO được quét SAU CÙNG luôn là PO sẽ được cân: quét nhầm PO1 rồi quét PO2 thì PO1 bị bỏ, số cân lưu cho PO2.
/// - Mã không đúng dạng PO không làm ảnh hưởng PO đang chờ cân.
/// - Lưu xong thì xóa PO gần nhất và chờ quét PO mới.
/// - PO đã cân trước đó mà cân lại thì cập nhật số kg mới (kèm số kg cũ trong thông báo).
///
/// Có thể được gọi từ nhiều luồng (máy quét, cổng COM, UI); trạng thái được bảo vệ bằng khóa,
/// các sự kiện được phát ra ngoài khóa.
/// </summary>
public sealed class WeighingWorkflow
{
    private readonly object _gate = new();
    private readonly IPoRepository _poRepository;
    private readonly IHistoryRepository _history;
    private readonly IScaleReader _scale;
    private readonly IErrorLogWriter _errorLog;
    private readonly IPendingQueue _pending;
    private readonly Func<WeighingSettings> _settings;
    private readonly Func<DateTime> _clock;
    private readonly Action<string>? _log;

    private WorkflowState _state = WorkflowState.Idle;
    private PoInfo? _currentPo;
    private readonly List<PoInfo> _group = new();   // các PO cùng gói đang chờ cân; PO cuối = _currentPo
    private readonly Dictionary<string, double> _master = new();   // số lượng gốc (master) của từng PO trong gói
    private DateTime _lastScanAt = DateTime.MinValue;
    private string? _lookingUp;
    private string? _queuedCode;
    private double _lastReading;
    private int _generation;
    private StabilityDetector _stability = new(5, 0.005, 0.01);

    public WeighingWorkflow(
        IPoRepository poRepository,
        IHistoryRepository history,
        IScaleReader scale,
        IErrorLogWriter errorLog,
        IPendingQueue pending,
        Func<WeighingSettings> settings,
        Func<DateTime>? clock = null,
        Action<string>? log = null)
    {
        _poRepository = poRepository;
        _history = history;
        _scale = scale;
        _errorLog = errorLog;
        _pending = pending;
        _settings = settings;
        _clock = clock ?? (() => DateTime.Now);
        _log = log;
    }

    public event Action<WorkflowState>? StateChanged;
    public event Action<PoInfo>? PoLoaded;

    /// <summary>PO gần nhất bị xóa (đã cân xong, bị thay bằng PO khác, hết thời gian chờ...): giao diện xóa thông tin PO.</summary>
    public event Action? PoCleared;

    /// <summary>Hai PO trở lên cùng custno được ghép thành một gói: số PO trong gói và tổng khối lượng chuẩn (nếu đủ dữ liệu).</summary>
    public event Action<IReadOnlyList<PoInfo>, double?>? GroupFormed;

    public event Action<WorkflowNotice>? Notice;
    public event Action<double, bool>? WeightChanged;
    public event Action<WeighingOutcome>? Completed;

    /// <summary>Người dùng bấm Reset: xóa toàn bộ trạng thái hiển thị.</summary>
    public event Action? Cleared;

    public WorkflowState State
    {
        get { lock (_gate) return _state; }
    }

    public PoInfo? CurrentPo
    {
        get { lock (_gate) return _currentPo; }
    }

    /// <summary>
    /// Người dùng chỉnh số lượng (pcs) của một PO đang chờ cân vì số lượng thực tế khác master. Số đã chỉnh được dùng để
    /// tính khối lượng chuẩn, chia số cân (gói nhiều PO) và lưu vào lịch sử. Chỉ chỉnh được khi đang chờ cân.
    /// Trả về khối lượng chuẩn mới (cả gói nếu là gói nhiều PO; null nếu thiếu dữ liệu đóng gói).
    /// </summary>
    public bool TrySetQuantity(string aufnr, double quantity, out double? expectedKg)
    {
        expectedKg = null;
        if (!(quantity > 0) || double.IsInfinity(quantity) || quantity > 1_000_000)
            return false;

        lock (_gate)
        {
            if (_state != WorkflowState.WaitingForWeight)
                return false;

            int index = _group.FindIndex(p => string.Equals(p.Aufnr, aufnr, StringComparison.Ordinal));
            if (index < 0)
                return false;

            var updated = _group[index] with { Quantity = quantity };
            _group[index] = updated;
            if (string.Equals(_currentPo?.Aufnr, aufnr, StringComparison.Ordinal))
                _currentPo = updated;

            expectedKg = _group.Count > 1
                ? ExpectedTotal(_group)
                : WeightCalculator.ExpectedKg(updated.NetWeight, updated.PackQty, updated.Quantity);
            return true;
        }
    }

    /// <summary>Gọi mỗi khi quét hoặc nhập một mã PO.</summary>
    public async Task HandleCodeAsync(string rawCode)
    {
        var s = _settings();

        // Mã không đúng dạng PO: chỉ cảnh báo, không đụng tới PO đang chờ cân.
        if (!PoParser.TryParse(rawCode, s.MinPlainCodeLength, out var aufnr))
        {
            Notify(NoticeLevel.Warning, $"PO không hợp lệ: {Shorten(rawCode)}");
            return;
        }

        int startGen;
        bool hadPo;
        List<PoInfo>? prior = null;   // gói đang chờ cân, giữ lại đến khi biết PO mới có cùng custno không
        lock (_gate)
        {
            if (_state == WorkflowState.Saving)
            {
                // Đang lưu PO trước: nhớ mã mới và xử lý ngay sau khi lưu xong.
                _queuedCode = rawCode;
                return;
            }

            if (_state == WorkflowState.WaitingForWeight && _group.Any(p => string.Equals(p.Aufnr, aufnr, StringComparison.Ordinal)))
                return; // quét lại đúng PO đang chờ cân: giữ nguyên tiến độ

            if (_state == WorkflowState.LookingUpPo && string.Equals(_lookingUp, aufnr, StringComparison.Ordinal))
                return; // đang tra đúng mã này

            var now = _clock();
            bool withinWindow = s.GroupWindowSeconds > 0
                && _state == WorkflowState.WaitingForWeight
                && _currentPo != null
                && (now - _lastScanAt).TotalSeconds <= s.GroupWindowSeconds;
            _lastScanAt = now;

            if (withinWindow)
            {
                // Quét liên tiếp trong thời gian ghép: chưa bỏ PO cũ, đợi tra PO mới rồi so custno.
                prior = _group.ToList();
                hadPo = false;
            }
            else
            {
                // PO quét sau cùng thắng: bỏ PO đang chờ (nếu có) và bắt đầu tra PO mới.
                hadPo = _currentPo != null;
                if (hadPo)
                    _log?.Invoke($"Quét PO {aufnr}, bỏ PO {_currentPo!.Aufnr} chưa cân");
                _currentPo = null;
                _group.Clear();
            }

            _generation++;
            startGen = _generation;
            _lookingUp = aufnr;
            _lastReading = 0;
            _queuedCode = null;
            _state = WorkflowState.LookingUpPo;
        }

        if (hadPo)
            Raise(() => PoCleared?.Invoke());
        Raise(() => StateChanged?.Invoke(WorkflowState.LookingUpPo));

        PoInfo? po;
        try
        {
            po = await _poRepository.FindAsync(aufnr, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Tra PO lỗi: " + ex);
            if (IsCurrent(startGen))
            {
                Notify(NoticeLevel.Error, "Không truy vấn được dữ liệu PO: " + ex.Message);
                SetIdle(startGen, prior != null);
            }

            return;
        }

        if (po is null)
        {
            if (IsCurrent(startGen))
            {
                Notify(NoticeLevel.Warning, $"Không tìm thấy PO {aufnr}");
                SetIdle(startGen, prior != null);
            }

            return;
        }

        int gen;
        bool joined = false;
        List<PoInfo> members;
        lock (_gate)
        {
            if (_generation != startGen)
                return; // đã có PO khác được quét (hoặc Reset) trong lúc tra cứu: bỏ kết quả này

            joined = prior != null && SameCustomer(prior, po);
            members = joined ? new List<PoInfo>(prior!) { po } : new List<PoInfo> { po };
            if (!joined)
                _master.Clear();
            _master[po.Aufnr] = po.Quantity;   // số lượng theo master; người dùng có thể chỉnh sau (TrySetQuantity)
            _group.Clear();
            _group.AddRange(members);
            _currentPo = po;
            _lookingUp = null;
            _stability = new StabilityDetector(s.StableCount, s.StableSpreadKg, s.MinWeightKg);
            _lastReading = 0;
            _state = WorkflowState.WaitingForWeight;
            gen = ++_generation;
        }

        if (prior != null && !joined)
        {
            _log?.Invoke($"PO {po.Aufnr} khác custno với PO đang chờ: bỏ PO cũ");
            Raise(() => PoCleared?.Invoke());
        }

        Raise(() => PoLoaded?.Invoke(po));
        if (joined)
        {
            double? expectedTotal = ExpectedTotal(members);
            _log?.Invoke($"Ghép gói {string.Join("+", members.Select(m => m.Aufnr))} (custno {po.CustNo})");
            Notify(NoticeLevel.Info,
                $"Ghép {members.Count} PO cùng custno {po.CustNo}: {string.Join(" + ", members.Select(m => $"{m.Aufnr} ({m.Quantity:0.###} pcs)"))}. Cân một lần, số cân sẽ chia theo pcs.");
            Raise(() => GroupFormed?.Invoke(members, expectedTotal));
        }

        Raise(() => StateChanged?.Invoke(WorkflowState.WaitingForWeight));
        _scale.StartPolling();
        _ = WaitTimeoutAsync(gen, s.WaitTimeoutSeconds);
    }

    /// <summary>Gọi mỗi khi cân trả về một số.</summary>
    public async Task OnScaleReadingAsync(double kg)
    {
        bool stable;
        double value;
        int gen;

        lock (_gate)
        {
            if (_state != WorkflowState.WaitingForWeight)
                return;

            _lastReading = kg;
            stable = _stability.Add(kg, out value);
            if (stable)
                _state = WorkflowState.Saving;
            gen = _generation;
        }

        Raise(() => WeightChanged?.Invoke(kg, stable));

        if (stable)
        {
            Raise(() => StateChanged?.Invoke(WorkflowState.Saving));
            await SaveAsync(value, gen).ConfigureAwait(false);
        }
    }

    /// <summary>Nút "Xác nhận cân": lưu ngay số cân hiện tại, không chờ ổn định.</summary>
    public async Task ConfirmAsync()
    {
        var s = _settings();
        double reading;
        int gen;
        bool ok;

        lock (_gate)
        {
            reading = _lastReading;
            gen = _generation;
            ok = _state == WorkflowState.WaitingForWeight && reading >= s.MinWeightKg;
            if (ok)
                _state = WorkflowState.Saving;
        }

        if (!ok)
        {
            Notify(NoticeLevel.Info, State == WorkflowState.Idle
                ? "Chưa có PO. Hãy quét mã trước."
                : "Chưa nhận được số cân.");
            return;
        }

        Raise(() => StateChanged?.Invoke(WorkflowState.Saving));
        await SaveAsync(Math.Round(reading, 3), gen).ConfigureAwait(false);
    }

    /// <summary>Xóa PO hiện tại, hủy chờ cân và đưa mọi thứ về trạng thái sẵn sàng.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _state = WorkflowState.Idle;
            _currentPo = null;
            _group.Clear();
            _lookingUp = null;
            _queuedCode = null;
            _lastReading = 0;
        }

        _scale.StopPolling();
        Raise(() => StateChanged?.Invoke(WorkflowState.Idle));
        Raise(() => Cleared?.Invoke());
    }

    private async Task SaveAsync(double kg, int gen)
    {
        PoInfo po;
        List<PoInfo> group;
        Dictionary<string, double> masters;
        lock (_gate)
        {
            po = _currentPo!;
            group = _group.ToList();
            masters = new Dictionary<string, double>(_master);
        }

        string editNote = EditedNote(group, masters);

        if (group.Count > 1)
        {
            await SaveGroupAsync(group, kg, gen, editNote).ConfigureAwait(false);
            return;
        }

        _scale.StopPolling();
        var s = _settings();
        // Số lưu = số cân trừ phần bì của PO; so dung sai vẫn dùng số cân thô.
        double deduct = Math.Max(0, s.DeductPerPo);
        double savedKg = Math.Max(0, Math.Round(kg - deduct, 3));
        string deductNote = deduct > 0 ? $" Số lưu {savedKg:0.###} sau khi trừ {deduct:0.###}." : "";
        var record = new WeighingRecord(0, po.Aufnr, po.Phcd, po.Phtx, po.Pstx, po.Quantity, savedKg, _clock());

        WeightCheckResult? check = null;
        var expected = WeightCalculator.ExpectedKg(po.NetWeight, po.PackQty, po.Quantity);
        if (expected is double e)
            check = WeightCalculator.Check(kg, e, s.ToleranceKg, s.TolerancePercent);

        SaveOutcome saved;
        try
        {
            saved = await _history.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DataUnavailableException ex)
        {
            _log?.Invoke("Mất kết nối SQL khi lưu: " + ex.Message);
            _pending.Enqueue(record);
            saved = new SaveOutcome(SaveResult.Queued);
            Notify(NoticeLevel.Warning, "Mất kết nối SQL - đã lưu tạm trên máy, sẽ tự đồng bộ khi có mạng lại.");
        }
        catch (Exception ex)
        {
            _log?.Invoke("Lưu lịch sử lỗi: " + ex);
            Notify(NoticeLevel.Error, "Lưu thất bại: " + ex.Message + " Hãy quét lại PO.");
            FinishPo(gen);
            await ProcessQueuedCodeAsync().ConfigureAwait(false);
            return;
        }

        bool updated = saved.Result == SaveResult.Updated;
        string updatedNote = updated && saved.PreviousKg is double prev
            ? $" (đã cập nhật cân mới, trước đó {prev:0.###} kg)"
            : "";

        if (check is { WithinTolerance: false })
        {
            try { _errorLog.WriteMismatch(record, po, check); }
            catch (Exception ex) { _log?.Invoke("Ghi log lệch cân lỗi: " + ex.Message); }

            Notify(NoticeLevel.Warning,
                $"Lệch khối lượng PO {po.Aufnr}: cân {kg:0.###} kg, chuẩn {check.Expected:0.###} kg (cho phép ±{check.Allowed:0.###} kg){updatedNote}.{editNote}{deductNote}");
        }
        else if (updated)
        {
            Notify(NoticeLevel.Info, saved.PreviousKg is double p
                ? $"Đã cập nhật cân mới cho PO {po.Aufnr}: {kg:0.###} kg (trước đó {p:0.###} kg).{editNote}{deductNote}"
                : $"Đã cập nhật cân mới cho PO {po.Aufnr}: {kg:0.###} kg.{editNote}{deductNote}");
        }
        else if (saved.Result == SaveResult.Saved)
        {
            Notify(NoticeLevel.Success, check is null
                ? $"Đã cân {kg:0.###} kg (không có khối lượng chuẩn để so sánh).{editNote}{deductNote}"
                : $"Đã cân {kg:0.###} kg.{editNote}{deductNote}");
        }

        Raise(() => Completed?.Invoke(new WeighingOutcome(po, kg, saved.Result, check, saved.PreviousKg)));

        // Cân xong: xóa PO gần nhất, chờ PO mới.
        FinishPo(gen);
        await ProcessQueuedCodeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Lưu một gói nhiều PO cùng custno: một lần cân, số cân chia cho từng PO theo pcs (PO nhiều pcs nhận nhiều kg hơn).
    /// Dung sai so tổng cân với tổng khối lượng chuẩn của cả gói.
    /// </summary>
    private async Task SaveGroupAsync(List<PoInfo> group, double totalKg, int gen, string editNote)
    {
        _scale.StopPolling();
        var s = _settings();
        var now = _clock();
        // Mỗi sheet (PO) trừ DeductPerPo: trừ DeductPerPo × số PO khỏi tổng số cân rồi mới chia theo pcs.
        double deduct = Math.Max(0, s.DeductPerPo) * group.Count;
        double netKg = Math.Max(0, Math.Round(totalKg - deduct, 3));
        string deductNote = deduct > 0
            ? $" Đã trừ {deduct:0.###} ({Math.Max(0, s.DeductPerPo):0.###} × {group.Count} PO), số lưu tổng {netKg:0.###}."
            : "";
        var shares = WeightSplitter.Split(netKg, group.Select(p => p.Quantity).ToList());

        WeightCheckResult? check = null;
        if (ExpectedTotal(group) is double expectedTotal)
            check = WeightCalculator.Check(totalKg, expectedTotal, s.ToleranceKg, s.TolerancePercent);

        var saved = new List<PoShare>();
        var failed = new List<string>();
        string? failure = null;
        bool queued = false;

        for (int i = 0; i < group.Count; i++)
        {
            var po = group[i];
            var record = new WeighingRecord(0, po.Aufnr, po.Phcd, po.Phtx, po.Pstx, po.Quantity, shares[i], now);
            try
            {
                var outcome = await _history.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
                saved.Add(new PoShare(po.Aufnr, po.Quantity, shares[i], outcome.Result, outcome.PreviousKg));
            }
            catch (DataUnavailableException ex)
            {
                _log?.Invoke("Mất kết nối SQL khi lưu: " + ex.Message);
                _pending.Enqueue(record);
                queued = true;
                saved.Add(new PoShare(po.Aufnr, po.Quantity, shares[i], SaveResult.Queued));
            }
            catch (Exception ex)
            {
                _log?.Invoke("Lưu lịch sử lỗi: " + ex);
                failed.Add(po.Aufnr);
                failure = ex.Message;
            }
        }

        if (failed.Count > 0)
            Notify(NoticeLevel.Error, $"Lưu thất bại PO {string.Join(", ", failed)}: {failure} Hãy quét lại PO.");

        if (saved.Count > 0)
        {
            string names = string.Join("+", saved.Select(x => x.Aufnr));
            string breakdown = string.Join(", ", saved.Select(x => $"{x.Aufnr} = {x.Kg:0.###} kg ({x.Pcs:0.###} pcs)"));
            var updatedParts = saved.Where(x => x.Result == SaveResult.Updated)
                .Select(x => x.PreviousKg is double p ? $"{x.Aufnr} (trước đó {p:0.###} kg)" : x.Aufnr).ToList();
            string updatedNote = updatedParts.Count > 0 ? $" Đã cập nhật cân mới: {string.Join(", ", updatedParts)}." : "";

            if (queued)
                Notify(NoticeLevel.Warning, "Mất kết nối SQL - đã lưu tạm trên máy, sẽ tự đồng bộ khi có mạng lại.");

            if (check is { WithinTolerance: false })
            {
                var first = group[0];
                var combinedPo = first with
                {
                    Aufnr = names,
                    Quantity = group.Sum(p => p.Quantity)
                };
                var combinedRecord = new WeighingRecord(0, names, first.Phcd, first.Phtx, first.Pstx, combinedPo.Quantity, totalKg, now);
                try { _errorLog.WriteMismatch(combinedRecord, combinedPo, check); }
                catch (Exception ex) { _log?.Invoke("Ghi log lệch cân lỗi: " + ex.Message); }

                Notify(NoticeLevel.Warning,
                    $"Lệch khối lượng gói {names}: cân {totalKg:0.###} kg, chuẩn {check.Expected:0.###} kg (cho phép ±{check.Allowed:0.###} kg). Đã chia: {breakdown}.{deductNote}{updatedNote}{editNote}");
            }
            else
            {
                Notify(updatedParts.Count > 0 ? NoticeLevel.Info : NoticeLevel.Success,
                    $"Đã cân {totalKg:0.###} kg cho {saved.Count} PO: {breakdown}.{deductNote}{updatedNote}{editNote}");
            }

            var overall = saved.Any(x => x.Result == SaveResult.Queued) ? SaveResult.Queued
                : saved.Any(x => x.Result == SaveResult.Updated) ? SaveResult.Updated
                : SaveResult.Saved;
            Raise(() => Completed?.Invoke(new WeighingOutcome(group[^1], totalKg, overall, check, null, saved)));
        }

        FinishPo(gen);
        await ProcessQueuedCodeAsync().ConfigureAwait(false);
    }

    /// <summary>Kết thúc PO hiện tại: xóa PO, về trạng thái chờ quét (chỉ khi chưa có Reset/PO khác chen vào).</summary>
    private void FinishPo(int gen)
    {
        lock (_gate)
        {
            if (_generation != gen)
                return;

            _currentPo = null;
            _group.Clear();
            _lastReading = 0;
            _state = WorkflowState.Idle;
        }

        Raise(() => PoCleared?.Invoke());
        Raise(() => StateChanged?.Invoke(WorkflowState.Idle));
    }

    /// <summary>Nếu người dùng đã quét PO khác trong lúc đang lưu thì xử lý mã đó ngay bây giờ.</summary>
    private async Task ProcessQueuedCodeAsync()
    {
        string? code;
        lock (_gate)
        {
            code = _queuedCode;
            _queuedCode = null;
        }

        if (code != null)
            await HandleCodeAsync(code).ConfigureAwait(false);
    }

    private async Task WaitTimeoutAsync(int gen, int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, seconds))).ConfigureAwait(false);

        lock (_gate)
        {
            if (_generation != gen || _state != WorkflowState.WaitingForWeight)
                return;

            _generation++;
            _currentPo = null;
            _group.Clear();
            _state = WorkflowState.Idle;
        }

        _scale.StopPolling();
        Notify(NoticeLevel.Warning, "Hết thời gian chờ cân. Vui lòng quét lại mã.");
        Raise(() => PoCleared?.Invoke());
        Raise(() => StateChanged?.Invoke(WorkflowState.Idle));
    }

    private bool IsCurrent(int gen)
    {
        lock (_gate) return _generation == gen;
    }

    /// <summary>Tra PO thất bại: về chờ quét. <paramref name="dropGroup"/> = có gói cũ đang giữ lại thì bỏ nó và báo giao diện xóa PO.</summary>
    private void SetIdle(int gen, bool dropGroup = false)
    {
        lock (_gate)
        {
            if (_generation != gen)
                return;
            _lookingUp = null;
            _state = WorkflowState.Idle;
            if (dropGroup)
            {
                _currentPo = null;
                _group.Clear();
            }
        }

        if (dropGroup)
        {
            _scale.StopPolling();
            Raise(() => PoCleared?.Invoke());
        }

        Raise(() => StateChanged?.Invoke(WorkflowState.Idle));
    }

    /// <summary>Cùng custno (không rỗng, không phân biệt hoa thường) với mọi PO trong gói đang chờ.</summary>
    private static bool SameCustomer(IReadOnlyList<PoInfo> group, PoInfo po)
    {
        var cust = po.CustNo?.Trim();
        return !string.IsNullOrEmpty(cust)
            && group.All(p => string.Equals(p.CustNo?.Trim(), cust, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ghi chú các PO có số lượng bị chỉnh khác master, ví dụ " Số lượng đã chỉnh: 521 3→4 pcs."; rỗng nếu không có.</summary>
    private static string EditedNote(IEnumerable<PoInfo> group, IReadOnlyDictionary<string, double> masters)
    {
        var parts = group
            .Where(p => masters.TryGetValue(p.Aufnr, out var m) && Math.Abs(m - p.Quantity) > 1e-9)
            .Select(p => $"{p.Aufnr} {masters[p.Aufnr]:0.###}→{p.Quantity:0.###} pcs")
            .ToList();
        return parts.Count == 0 ? "" : $" Số lượng đã chỉnh: {string.Join(", ", parts)}.";
    }

    private static double? ExpectedTotal(IReadOnlyList<PoInfo> group)
    {
        double total = 0;
        foreach (var p in group)
        {
            if (WeightCalculator.ExpectedKg(p.NetWeight, p.PackQty, p.Quantity) is not double e)
                return null;
            total += e;
        }

        return Math.Round(total, 3);
    }

    private void Notify(NoticeLevel level, string message)
        => Raise(() => Notice?.Invoke(new WorkflowNotice(level, message)));

    private void Raise(Action action)
    {
        try { action(); }
        catch (Exception ex) { _log?.Invoke("Lỗi trong trình xử lý sự kiện workflow: " + ex); }
    }

    private static string Shorten(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
