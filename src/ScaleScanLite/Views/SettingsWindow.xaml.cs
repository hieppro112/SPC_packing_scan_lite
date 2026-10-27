using System.ComponentModel;
using System.IO.Ports;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Infrastructure;
using ScaleScanLite.Services;
using ScaleScanLite.Services.Input;

namespace ScaleScanLite.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly RawInputScanner _scanner;
    private readonly SettingsForm _form;

    internal SettingsWindow(SettingsService settings, RawInputScanner scanner)
    {
        InitializeComponent();

        _settings = settings;
        _scanner = scanner;
        _form = SettingsForm.From(settings.Current);
        DataContext = _form;

        try
        {
            PortCombo.ItemsSource = SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Không liệt kê được cổng COM: " + ex.Message);
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Cập nhật giá trị đang gõ dở trong ô nhập
        PortCombo.GetBindingExpression(System.Windows.Controls.ComboBox.TextProperty)?.UpdateSource();

        var updated = SettingsService.Clone(_settings.Current);
        var errors = _form.ApplyTo(updated);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, "Vui lòng sửa các mục sau:\n\n• " + string.Join("\n• ", errors),
                "Cài đặt chưa hợp lệ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Mật khẩu: để trống nghĩa là giữ nguyên mật khẩu đã lưu
        if (PoPassword.Password.Length > 0)
            updated.Database.PoPasswordProtected = SecretProtector.Protect(PoPassword.Password);
        if (HistoryPassword.Password.Length > 0)
            updated.Database.HistoryPasswordProtected = SecretProtector.Protect(HistoryPassword.Password);

        try
        {
            _settings.Save(updated);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không lưu được cấu hình:\n" + ex.Message, "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Chọn thư mục lưu log lệch cân" };
        if (dialog.ShowDialog(this) == true)
        {
            _form.ErrorLogFolder = dialog.FolderName;
            RefreshBindings();
        }
    }

    // ---------- Nhận diện máy quét ----------
    private void Learn_Click(object sender, RoutedEventArgs e)
    {
        // Máy quét "gõ" vào ô đang được chọn, nên đưa con trỏ vào ô thử riêng
        // và tạm bỏ nút Lưu mặc định để phím Enter cuối mã không bấm nhầm Lưu.
        LearnPanel.Visibility = Visibility.Visible;
        LearnStatus.Text = "Hãy quét một mã bằng máy quét USB...";
        LearnBox.Clear();
        LearnBox.Focus();
        SaveButton.IsDefault = false;

        _scanner.DeviceLearned -= OnDeviceLearned;
        _scanner.DeviceLearned += OnDeviceLearned;
        _scanner.StartLearning();
    }

    private void OnDeviceLearned(string deviceId, string sampleCode)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _form.ScannerDeviceId = deviceId;
            _form.ScannerEnabled = true;
            RefreshBindings();

            LearnPanel.Visibility = Visibility.Visible;
            LearnStatus.Text = $"Đã nhận diện máy quét: {deviceId}\nMã thử: {sampleCode}\nBấm Lưu để áp dụng.";
            SaveButton.IsDefault = true;
        });
    }

    private void LearnBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Tab)
            e.Handled = true;
    }

    private void RefreshBindings()
    {
        DataContext = null;
        DataContext = _form;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _scanner.DeviceLearned -= OnDeviceLearned;
        _scanner.StopLearning();
        base.OnClosing(e);
    }
}
