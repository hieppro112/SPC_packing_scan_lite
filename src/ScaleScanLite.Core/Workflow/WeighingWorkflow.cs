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
        lock (_gate)
        {
            if (_state == WorkflowState.Saving)
            {
                // Đang lưu PO trước: nhớ mã mới và xử lý ngay sau khi lưu xong.
                _queuedCode = rawCode;
                return;
            }

            if (_state == WorkflowState.WaitingForWeight && string.Equals(_currentPo?.Aufnr, aufnr, StringComparison.Ordinal))
                return; // quét lại đúng PO đang chờ cân: giữ nguyên tiến độ

            if (_state == WorkflowState.LookingUpPo && string.Equals(_lookingUp, aufnr, StringComparison.Ordinal))
                return; // đang tra đúng mã này

            // PO quét sau cùng thắng: bỏ PO đang chờ (nếu có) và bắt đầu tra PO mới.
            hadPo = _currentPo != null;
            if (hadPo)
                _log?.Invoke($"Quét PO {aufnr}, bỏ PO {_currentPo!.Aufnr} chưa cân");

            _generation++;
            startGen = _generation;
            _currentPo = null;
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
                SetIdle(startGen);
            }

            return;
        }

        if (po is null)
        {
            if (IsCurrent(startGen))
            {
                Notify(NoticeLevel.Warning, $"Không tìm thấy PO {aufnr}");
                SetIdle(startGen);
            }

            return;
        }

        int gen;
        lock (_gate)
        {
            if (_generation != startGen)
                return; // đã có PO khác được quét (hoặc Reset) trong lúc tra cứu: bỏ kết quả này

            _currentPo = po;
            _lookingUp = null;
            _stability = new StabilityDetector(s.StableCount, s.StableSpreadKg, s.MinWeightKg);
            _lastReading = 0;
            _state = WorkflowState.WaitingForWeight;
            gen = ++_generation;
        }

        Raise(() => PoLoaded?.Invoke(po));
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
        lock (_gate)
        {
            po = _currentPo!;
        }

        _scale.StopPolling();
        var s = _settings();
        var record = new WeighingRecord(0, po.Aufnr, po.Phcd, po.Phtx, po.Pstx, po.Quantity, kg, _clock());

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
                $"Lệch khối lượng PO {po.Aufnr}: cân {kg:0.###} kg, chuẩn {check.Expected:0.###} kg (cho phép ±{check.Allowed:0.###} kg){updatedNote}.");
        }
        else if (updated)
        {
            Notify(NoticeLevel.Info, saved.PreviousKg is double p
                ? $"Đã cập nhật cân mới cho PO {po.Aufnr}: {kg:0.###} kg (trước đó {p:0.###} kg)."
                : $"Đã cập nhật cân mới cho PO {po.Aufnr}: {kg:0.###} kg.");
        }
        else if (saved.Result == SaveResult.Saved)
        {
            Notify(NoticeLevel.Success, check is null
                ? $"Đã cân {kg:0.###} kg (không có khối lượng chuẩn để so sánh)."
                : $"Đã cân {kg:0.###} kg.");
        }

        Raise(() => Completed?.Invoke(new WeighingOutcome(po, kg, saved.Result, check, saved.PreviousKg)));

        // Cân xong: xóa PO gần nhất, chờ PO mới.
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

    private void SetIdle(int gen)
    {
        lock (_gate)
        {
            if (_generation != gen)
                return;
            _lookingUp = null;
            _state = WorkflowState.Idle;
        }

        Raise(() => StateChanged?.Invoke(WorkflowState.Idle));
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
