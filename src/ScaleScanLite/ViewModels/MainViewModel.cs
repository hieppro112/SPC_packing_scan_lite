using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Logic;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Core.Workflow;
using ScaleScanLite.Infrastructure;
using ScaleScanLite.Services;
using ScaleScanLite.Services.Demo;
using ScaleScanLite.Services.Input;
using ScaleScanLite.Services.Scale;

namespace ScaleScanLite.ViewModels;

internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const int NotifyWindowSeconds = 3;

    /// <summary>Cùng một mã đến từ hai đường (ô nhập đang được chọn và Raw Input) trong khoảng này chỉ tính một lần.</summary>
    private const int DuplicateWindowMs = 1500;

    private readonly SettingsService _settings;
    private readonly WeighingWorkflow _workflow;
    private readonly ScaleService _scale;
    private readonly IHistoryRepository _history;
    private readonly PendingQueue _pending;
    private readonly INoticeService _notices;
    private readonly RawInputScanner _scanner;
    private readonly DemoCodeFeeder? _demoFeeder;
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _toastTimer;
    private CancellationTokenSource? _loadCts;
    private readonly object _submitLock = new();
    private string _lastSubmittedCode = "";
    private long _lastSubmittedTick;
    private int _completedCount;
    private bool _initialized;
    private bool _disposed;

    public MainViewModel(
        SettingsService settings,
        WeighingWorkflow workflow,
        ScaleService scale,
        IHistoryRepository history,
        PendingQueue pending,
        INoticeService notices,
        RawInputScanner scanner,
        DemoCodeFeeder? demoFeeder = null)
    {
        _settings = settings;
        _workflow = workflow;
        _scale = scale;
        _history = history;
        _pending = pending;
        _notices = notices;
        _scanner = scanner;
        _demoFeeder = demoFeeder;
        _ui = Application.Current.Dispatcher;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_workflow.State == WorkflowState.Idle)
                await LoadPageAsync(CurrentPage);
            UpdatePending();
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(NotifyWindowSeconds) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            IsToastVisible = false;
        };

        Wire();
    }

    // ---------- Sự kiện để cửa sổ xử lý ----------
    public event Action? OpenSettingsRequested;
    public event Action? CompactRequested;

    /// <summary>Yêu cầu đưa con trỏ về ô nhập mã để lượt quét tiếp theo vào đúng ô.</summary>
    public event Action? FocusEntryRequested;

    public Func<string?>? PickExportPath { get; set; }

    // ---------- Nhập / quét mã ----------
    [ObservableProperty] private string _manualCode = "";
    [ObservableProperty] private string _lastCode = "—";
    [ObservableProperty] private string _scanTime = "—";
    [ObservableProperty] private bool _isScannerEnabled;
    [ObservableProperty] private bool _isScannerActive;
    [ObservableProperty] private string _scannerStatusText = "";

    // ---------- Cân ----------
    [ObservableProperty] private string _weightText = "0.000";
    [ObservableProperty] private bool _isWeightStable;
    [ObservableProperty] private bool _isScaleConnected;
    [ObservableProperty] private string _scaleStatusText = "Chưa kết nối";
    [ObservableProperty] private string _stateText = "Sẵn sàng - hãy quét mã";

    // ---------- PO hiện tại ----------
    [ObservableProperty] private string _poCode = "—";
    [ObservableProperty] private string _phcd = "—";
    [ObservableProperty] private string _phtx = "—";
    [ObservableProperty] private string _pstx = "—";
    [ObservableProperty] private string _quantityText = "—";
    [ObservableProperty] private string _expectedKgText = "—";

    // ---------- Kết quả cân gần nhất ----------
    [ObservableProperty] private bool _hasLastResult;
    [ObservableProperty] private string _lastResultText = "";
    [ObservableProperty] private NoticeLevel _lastResultLevel = NoticeLevel.Info;

    // ---------- Trạng thái chung ----------
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isDbOnline = true;
    [ObservableProperty] private string _dbStatusText = "SQL: chưa kiểm tra";
    [ObservableProperty] private string _pendingText = "";
    [ObservableProperty] private bool _isCompact;
    [ObservableProperty] private bool _isToastVisible;
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private NoticeLevel _toastLevel;

    // ---------- Lịch sử ----------
    public ObservableCollection<WeighingRecord> Rows { get; } = new();
    public int[] PageSizes { get; } = { 5, 10, 15, 20 };

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private int _selectedPageSize = 10;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private string _pageSummary = "Trang 1 / 1";
    [ObservableProperty] private string _rangeSummary = "";
    [ObservableProperty] private bool _canGoPrevious;
    [ObservableProperty] private bool _canGoNext;
    private int _totalPages = 1;

    partial void OnSelectedPageSizeChanged(int value)
    {
        if (!_initialized)
            return;

        Mutate(s => s.Ui.PageSize = value);
        _ = LoadPageAsync(1);
    }

    // =====================================================================
    // Khởi động
    // =====================================================================
    public async Task InitializeAsync()
    {
        SelectedPageSize = _settings.Current.Ui.PageSize;
        StatusMessage = "Đang khởi động...";
        _initialized = true;

        _scanner.Start();
        ApplyScannerSettings();

        _scale.Start();
        await LoadPageAsync(1);
        UpdatePending();
        _refreshTimer.Start();

        _demoFeeder?.Start();

        if (string.IsNullOrEmpty(StatusMessage) || StatusMessage == "Đang khởi động...")
            StatusMessage = _settings.IsDemo ? "Chế độ chạy thử: mã quét giả lập sẽ tự xuất hiện" : "Sẵn sàng - quét mã hoặc nhập mã PO";

        FocusEntryRequested?.Invoke();
    }

    private void Wire()
    {
        _workflow.StateChanged += state => Post(() => StateText = state switch
        {
            WorkflowState.LookingUpPo => "Đang tra cứu PO...",
            WorkflowState.WaitingForWeight => "Đặt hàng lên cân...",
            WorkflowState.Saving => "Đang lưu...",
            _ => "Sẵn sàng - hãy quét mã"
        });
        _workflow.PoLoaded += po => Post(() => ApplyPo(po));
        _workflow.Notice += notice => Post(() => ShowNotice(notice));
        _workflow.WeightChanged += (_, stable) => Post(() => IsWeightStable = stable);
        _workflow.Completed += outcome => Post(() =>
        {
            _completedCount++;
            ShowLastResult(outcome);
            UpdatePending();
            _ = LoadPageAsync(1);
            FocusEntryRequested?.Invoke();
        });
        _workflow.Cleared += () => Post(ClearPo);

        _scale.ReadingReceived += kg =>
        {
            Post(() => WeightText = kg.ToString("0.000"));
            _ = RunSafe(() => _workflow.OnScaleReadingAsync(kg), "xử lý số cân");
        };
        _scale.ConnectionChanged += (connected, message) => Post(() =>
        {
            IsScaleConnected = connected;
            ScaleStatusText = connected ? "Đã kết nối" : "Chưa kết nối";
            if (!connected)
                IsWeightStable = false;
        });

        _scanner.CodeScanned += (code, deviceId) => SubmitCode(code);

        if (_demoFeeder != null)
            _demoFeeder.CodeScanned += code => SubmitCode(code);

        _pending.Synced += count => Post(() =>
        {
            UpdatePending();
            StatusMessage = $"Đã đồng bộ {count} bản ghi lưu tạm lên SQL";
            _ = LoadPageAsync(CurrentPage);
        });

        _settings.Changed += () => Post(OnSettingsChanged);
    }

    // =====================================================================
    // Nhận mã (máy quét nghe nền, ô nhập, chế độ chạy thử)
    // =====================================================================
    [RelayCommand]
    private void SubmitManualCode()
    {
        var code = ManualCode.Trim();
        ManualCode = "";
        FocusEntryRequested?.Invoke();

        if (code.Length > 0)
            SubmitCode(code);
    }

    /// <summary>
    /// Mọi nguồn mã đều đi qua đây. force = true: quét/nhập tay là thao tác cố ý nên cho phép quét lại đúng mã vừa xử lý.
    /// Cùng một mã đến từ hai đường trong một thời gian ngắn (máy quét gõ vào ô nhập + Raw Input) chỉ tính một lần.
    /// </summary>
    private void SubmitCode(string code)
    {
        long now = Environment.TickCount64;
        lock (_submitLock)
        {
            if (code == _lastSubmittedCode && now - _lastSubmittedTick < DuplicateWindowMs)
                return;

            _lastSubmittedCode = code;
            _lastSubmittedTick = now;
        }

        Post(() => LastCode = code.Length > 60 ? code[..60] + "…" : code);
        _ = RunSafe(() => _workflow.HandleCodeAsync(code, force: true), "xử lý mã quét");
    }

    // =====================================================================
    // Nghiệp vụ cân
    // =====================================================================
    [RelayCommand]
    private Task ConfirmAsync() => RunSafe(_workflow.ConfirmAsync, "xác nhận cân");

    [RelayCommand]
    private void Reset()
    {
        _workflow.Reset();
        StatusMessage = "Đã làm mới, sẵn sàng quét";
        FocusEntryRequested?.Invoke();
    }

    private void ApplyPo(PoInfo po)
    {
        PoCode = po.Aufnr;
        Phcd = Dash(po.Phcd);
        Phtx = Dash(po.Phtx);
        Pstx = Dash(po.Pstx);
        QuantityText = po.Quantity.ToString("0.###");
        ScanTime = DateTime.Now.ToString("HH:mm:ss");
        IsWeightStable = false;

        var expected = WeightCalculator.ExpectedKg(po.NetWeight, po.PackQty, po.Quantity);
        ExpectedKgText = expected is double e ? $"{e:0.###} kg" : "Chưa có dữ liệu";
    }

    private void ClearPo()
    {
        LastCode = "—";
        ScanTime = "—";
        PoCode = Phcd = Phtx = Pstx = QuantityText = ExpectedKgText = "—";
        WeightText = "0.000";
        IsWeightStable = false;
    }

    private void ShowLastResult(WeighingOutcome outcome)
    {
        var (text, level) = outcome.Result switch
        {
            SaveResult.Duplicate => ($"PO {outcome.Po.Aufnr} đã được cân trước đó, không lưu lại.", NoticeLevel.Warning),
            SaveResult.Queued => ($"PO {outcome.Po.Aufnr}: {outcome.Kg:0.###} kg - mất kết nối SQL, đã lưu tạm trên máy.", NoticeLevel.Warning),
            _ when outcome.Check is { WithinTolerance: false } c =>
                ($"PO {outcome.Po.Aufnr}: cân {outcome.Kg:0.###} kg, chuẩn {c.Expected:0.###} kg (lệch {c.Difference:+0.###;-0.###}).", NoticeLevel.Warning),
            _ when outcome.Check is { } c =>
                ($"PO {outcome.Po.Aufnr}: {outcome.Kg:0.###} kg - đúng khối lượng chuẩn {c.Expected:0.###} kg.", NoticeLevel.Success),
            _ => ($"PO {outcome.Po.Aufnr}: {outcome.Kg:0.###} kg - đã lưu (không có khối lượng chuẩn để so sánh).", NoticeLevel.Success)
        };

        LastResultText = text;
        LastResultLevel = level;
        HasLastResult = true;
    }

    private static string Dash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private void ShowNotice(WorkflowNotice notice)
    {
        StatusMessage = notice.Message;

        if (IsCompact)
        {
            ToastMessage = notice.Message;
            ToastLevel = notice.Level;
            IsToastVisible = true;
            _toastTimer.Stop();
            _toastTimer.Start();
        }
        else
        {
            _notices.Show(notice.Level, notice.Message);
        }
    }

    // =====================================================================
    // Lịch sử
    // =====================================================================
    [RelayCommand]
    private Task SearchAsync() => LoadPageAsync(1);

    [RelayCommand]
    private Task RefreshAsync() => LoadPageAsync(CurrentPage);

    [RelayCommand]
    private Task FirstPageAsync() => LoadPageAsync(1);

    [RelayCommand]
    private Task PreviousPageAsync() => LoadPageAsync(CurrentPage - 1);

    [RelayCommand]
    private Task NextPageAsync() => LoadPageAsync(CurrentPage + 1);

    [RelayCommand]
    private Task LastPageAsync() => LoadPageAsync(_totalPages);

    private async Task LoadPageAsync(int page)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, _settings.Current.Database.TimeoutSeconds * 2)));
        Interlocked.Exchange(ref _loadCts, cts)?.Cancel();

        try
        {
            var contains = _settings.Current.Ui.SearchContains;
            int size = SelectedPageSize;
            page = Math.Max(1, page);

            var result = await _history.QueryAsync(SearchText, contains, page, size, cts.Token);
            var info = PageInfo.Create(page, size, result.TotalItems);

            if (info.Page != page)
                result = await _history.QueryAsync(SearchText, contains, info.Page, size, cts.Token);

            Rows.Clear();
            foreach (var row in result.Items)
                Rows.Add(row);

            CurrentPage = info.Page;
            _totalPages = Math.Max(1, info.TotalPages);
            PageSummary = $"Trang {info.Page} / {_totalPages}";
            RangeSummary = info.TotalItems == 0 ? "Chưa có dữ liệu" : $"Hiển thị {info.FromIndex} – {info.ToIndex} trên {info.TotalItems}";
            CanGoPrevious = info.HasPrevious;
            CanGoNext = info.HasNext;
            SetDb(true);
        }
        catch (OperationCanceledException)
        {
            // đã có yêu cầu tải mới hơn
        }
        catch (DataUnavailableException ex)
        {
            AppLog.Warn("Tải lịch sử thất bại (mất kết nối): " + ex.Message);
            SetDb(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Tải lịch sử thất bại", ex);
            SetDb(false, ex.Message);
            StatusMessage = "Không tải được lịch sử: " + ex.Message;
        }
        finally
        {
            Interlocked.CompareExchange(ref _loadCts, null, cts);
        }
    }

    private void SetDb(bool online, string? error = null)
    {
        IsDbOnline = online;
        DbStatusText = online
            ? "SQL: đã kết nối"
            : error == null ? "SQL: mất kết nối" : "SQL: lỗi - " + error;
    }

    private void UpdatePending()
    {
        var count = _pending.Count;
        PendingText = count > 0 ? $"{count} bản ghi chờ đồng bộ" : "";
    }

    [RelayCommand]
    private async Task ExportExcelAsync()
    {
        var path = PickExportPath?.Invoke();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            StatusMessage = "Đang xuất Excel...";
            var rows = await _history.QueryAllAsync(SearchText, _settings.Current.Ui.SearchContains, 100_000, CancellationToken.None);
            await Task.Run(() => ExcelExporter.Export(rows, path));
            ShowNotice(new WorkflowNotice(NoticeLevel.Success, $"Đã xuất {rows.Count} dòng ra:\n{path}"));
        }
        catch (Exception ex)
        {
            AppLog.Error("Xuất Excel thất bại", ex);
            ShowNotice(new WorkflowNotice(NoticeLevel.Error, "Xuất Excel thất bại: " + ex.Message));
        }
    }

    // =====================================================================
    // Cửa sổ / cấu hình
    // =====================================================================
    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke();

    [RelayCommand]
    private void Compact() => CompactRequested?.Invoke();

    private void OnSettingsChanged()
    {
        ApplyScannerSettings();
        _ = ApplySettingsAsync();
    }

    private void ApplyScannerSettings()
    {
        var s = _settings.Current.Scanner;
        _scanner.Configure(s);

        IsScannerEnabled = s.Enabled;
        IsScannerActive = s.Enabled && !string.IsNullOrWhiteSpace(s.DeviceId);
        ScannerStatusText = !s.Enabled
            ? ""
            : IsScannerActive
                ? $"Máy quét USB: đang nghe ({s.DeviceId})"
                : "Máy quét USB: chưa nhận diện thiết bị";
    }

    private async Task ApplySettingsAsync()
    {
        await Task.Run(_scale.Restart);   // đóng/mở lại cổng COM có thể mất vài giây, không chặn giao diện

        var s = _settings.Current;
        if (SelectedPageSize != s.Ui.PageSize)
            SelectedPageSize = s.Ui.PageSize;

        await LoadPageAsync(1);
        StatusMessage = "Đã áp dụng cấu hình mới";
    }

    private void Mutate(Action<AppSettings> change)
    {
        var copy = SettingsService.Clone(_settings.Current);
        change(copy);
        _settings.Save(copy, notify: false);
    }

    // =====================================================================
    // Tiện ích
    // =====================================================================
    private void Post(Action action)
    {
        if (_disposed)
            return;

        try
        {
            _ui.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Không đưa được thao tác lên luồng giao diện: " + ex.Message);
        }
    }

    private static async Task RunSafe(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Lỗi khi " + what, ex);
        }
    }

    /// <summary>Báo cáo ngắn dùng cho chế độ tự kiểm tra (--selftest): cần cân nối, SQL ổn, có lịch sử và ít nhất một lượt cân hoàn tất.</summary>
    public (bool Ok, string Report) SelfTestReport()
    {
        bool ok = IsScaleConnected && Rows.Count > 0 && IsDbOnline && _completedCount > 0;
        var report = $"cân={(IsScaleConnected ? "nối" : "không nối")}; SQL={(IsDbOnline ? "ok" : "lỗi")}; dòng lịch sử={Rows.Count}; " +
                     $"lượt cân hoàn tất={_completedCount}; mã vừa quét={LastCode}; PO={PoCode}; trạng thái={StateText}; kết quả gần nhất={LastResultText}";
        return (ok, report);
    }

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer.Stop();
        _toastTimer.Stop();
        _loadCts?.Cancel();
        _demoFeeder?.Dispose();
    }
}
