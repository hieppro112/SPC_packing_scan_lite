using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ScaleScanLite.Infrastructure;
using ScaleScanLite.Services;
using ScaleScanLite.Services.Input;
using ScaleScanLite.ViewModels;

namespace ScaleScanLite.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly SettingsService _settings;
    private readonly RawInputScanner _scanner;
    private MiniWindow? _mini;
    private bool _closing;

    internal MainWindow(MainViewModel viewModel, SettingsService settings, RawInputScanner scanner)
    {
        InitializeComponent();

        _vm = viewModel;
        _settings = settings;
        _scanner = scanner;
        DataContext = viewModel;

        _vm.OpenSettingsRequested += OpenSettings;
        _vm.CompactRequested += EnterCompact;
        _vm.FocusEntryRequested += FocusEntry;
        _vm.PickExportPath = PickExportPath;

        // Quay lại cửa sổ này là sẵn sàng quét ngay (máy quét gõ vào ô nhập mã)
        Activated += (_, _) => FocusEntry();

        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.InitializeAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("Lỗi khởi tạo màn hình chính", ex);
                MessageBox.Show(this, "Khởi tạo gặp lỗi:\n" + ex.Message + "\n\nChi tiết: " + AppLog.FilePath,
                    "Scale Scan Lite", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        StateChanged += OnStateChanged;
    }

    /// <summary>Đưa con trỏ vào ô nhập mã, trừ khi người dùng đang gõ ở ô khác (ví dụ ô tìm lịch sử).</summary>
    private void FocusEntry()
    {
        if (!IsVisible)
            return;

        if (Keyboard.FocusedElement is TextBox other && !ReferenceEquals(other, EntryBox) && !other.IsReadOnly && IsActive)
            return;

        EntryBox.Focus();
    }

    // ---------- Nút cửa sổ ----------
    private void Minimize_Click(object sender, RoutedEventArgs e) => EnterCompact();

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // Cửa sổ tràn viền ~8px khi phóng to với WindowChrome: bù lại để không mất nội dung.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
        BtnMaximize.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

        // Windows tự thu nhỏ (Win+Down, taskbar...) cũng chuyển sang chế độ góc màn hình.
        if (WindowState == WindowState.Minimized && !_vm.IsCompact && !_closing)
        {
            WindowState = WindowState.Normal;
            EnterCompact();
        }
    }

    // ---------- Chế độ thu nhỏ góc màn hình ----------
    private void EnterCompact()
    {
        if (_vm.IsCompact)
            return;

        _vm.IsCompact = true;

        if (_mini == null)
        {
            _mini = new MiniWindow { DataContext = _vm };
            _mini.RestoreRequested += ExitCompact;
            _mini.Closed += (_, _) =>
            {
                _mini = null;
                // Đóng cửa sổ mini thì hiện lại cửa sổ chính, tránh tiến trình chạy ngầm không có cửa sổ nào.
                if (!_closing && _vm.IsCompact)
                    ExitCompact();
            };
        }

        _mini.SnapToBottomRight();
        _mini.Show();
        Hide();
    }

    private void ExitCompact()
    {
        if (!_vm.IsCompact)
            return;

        _vm.IsCompact = false;
        _vm.IsToastVisible = false;
        _mini?.Hide();
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        _mini?.Close();
        base.OnClosing(e);
    }

    // ---------- Điều hướng ----------
    private void NavDashboard_Click(object sender, RoutedEventArgs e) => Scroller.ScrollToTop();

    private void NavHistory_Click(object sender, RoutedEventArgs e) => HistoryCard.BringIntoView();

    /// <summary>DataGrid không tự cuộn nên chuyển con lăn chuột cho ScrollViewer của trang.</summary>
    private void Grid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - e.Delta);
    }

    // ---------- Hộp thoại ----------
    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings, _scanner) { Owner = this };
        window.ShowDialog();
    }

    private string? PickExportPath()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Xuất lịch sử cân ra Excel",
            Filter = "Excel (*.xlsx)|*.xlsx",
            FileName = $"lich-su-can-{DateTime.Now:yyyyMMdd-HHmm}.xlsx"
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
