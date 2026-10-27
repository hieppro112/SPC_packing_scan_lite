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
/// Luồng nghiệp vụ: quét mã → tra PO → chờ số cân ổn định → lưu → so khối lượng chuẩn.
/// Có thể được gọi từ nhiều luồng (camera, cổng COM, UI); trạng thái được bảo vệ bằng khóa,
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
    private string? _lastCode;
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
    public event Action<WorkflowNotice>? Notice;
    public event Action<double, bool>? WeightChanged;
    public event Action<WeighingOutcome>? Completed;
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
    /// Gọi mỗi khi đọc được một mã. Bỏ qua nếu đang bận. Mã trùng với mã vừa xử lý cũng bị bỏ qua
    /// (camera nhìn thấy cùng một tem liên tục) trừ khi <paramref name="force"/> = true,
    /// dùng cho máy quét cầm tay vì đó là thao tác cố ý của người dùng.
    /// </summary>
    public async Task HandleCodeAsync(string rawCode, bool force = false)
    {
        var s = _settings();
        int startGen;

        lock (_gate)
        {
            if (_state != WorkflowState.Idle)
                return;

            if (!force && string.Equals(rawCode, _lastCode, StringComparison.Ordinal))
                return;

            _lastCode = rawCode;
            _state = WorkflowState.LookingUpPo;
            startGen = _generation;
        }

        Raise(() => StateChanged?.Invoke(WorkflowState.LookingUpPo));

        if (!PoParser.TryParse(rawCode, s.MinPlainCodeLength, out var aufnr))
        {
            Notify(NoticeLevel.Warning, $"PO không hợp lệ: {Shorten(rawCode)}");
            SetIdle(startGen);
            return;
        }

        PoInfo? po;
        try
        {
            po = await _poRepository.FindAsync(aufnr, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log?.Invoke("Tra PO lỗi: " + ex);
            Notify(NoticeLevel.Error, "Không truy vấn được dữ liệu PO: " + ex.Message);
            lock (_gate) { if (_generation == startGen) _lastCode = null; }
            SetIdle(startGen);
            return;
        }

        if (po is null)
        {
            Notify(NoticeLevel.Warning, $"Không tìm thấy PO {aufnr}");
            SetIdle(startGen);
            return;
        }

        int gen;
        lock (_gate)
        {
            if (_generation != startGen)
                return; // đã Reset trong lúc tra cứu

            _currentPo = po;
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

    /// <summary>Xóa PO hiện tại và cho phép quét lại cả mã vừa quét.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _state = WorkflowState.Idle;
            _currentPo = null;
            _lastCode = null;
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

        SaveResult result;
        try
        {
            result = await _history.InsertAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DataUnavailableException ex)
        {
            _log?.Invoke("Mất kết nối SQL khi lưu: " + ex.Message);
            _pending.Enqueue(record);
            result = SaveResult.Queued;
            Notify(NoticeLevel.Warning, "Mất kết nối SQL - đã lưu tạm trên máy, sẽ tự đồng bộ khi có mạng lại.");
        }
        catch (Exception ex)
        {
            _log?.Invoke("Lưu lịch sử lỗi: " + ex);
            Notify(NoticeLevel.Error, "Lưu thất bại: " + ex.Message);
            lock (_gate) { if (_generation == gen) _lastCode = null; }
            SetIdle(gen);
            return;
        }

        if (result == SaveResult.Duplicate)
        {
            Notify(NoticeLevel.Warning, $"PO {po.Aufnr} đã được cân trước đó, không lưu lại.");
        }
        else
        {
            if (check is { WithinTolerance: false })
            {
                try { _errorLog.WriteMismatch(record, po, check); }
                catch (Exception ex) { _log?.Invoke("Ghi log lệch cân lỗi: " + ex.Message); }

                Notify(NoticeLevel.Warning,
                    $"Lệch khối lượng: cân {kg:0.###} kg, chuẩn {check.Expected:0.###} kg (cho phép ±{check.Allowed:0.###} kg).");
            }
            else if (result == SaveResult.Saved)
            {
                Notify(NoticeLevel.Success, check is null
                    ? $"Đã cân {kg:0.###} kg (không có khối lượng chuẩn để so sánh)."
                    : $"Đã cân {kg:0.###} kg.");
            }
        }

        Raise(() => Completed?.Invoke(new WeighingOutcome(po, kg, result, check)));
        SetIdle(gen);
    }

    private async Task WaitTimeoutAsync(int gen, int seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, seconds))).ConfigureAwait(false);

        lock (_gate)
        {
            if (_generation != gen || _state != WorkflowState.WaitingForWeight)
                return;

            _generation++;
            _state = WorkflowState.Idle;
            _lastCode = null;
        }

        _scale.StopPolling();
        Notify(NoticeLevel.Warning, "Hết thời gian chờ cân. Vui lòng quét lại mã.");
        Raise(() => StateChanged?.Invoke(WorkflowState.Idle));
    }

    private void SetIdle(int gen)
    {
        lock (_gate)
        {
            if (_generation != gen)
                return;
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
