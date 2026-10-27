using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ScaleScanLite.Core.Workflow;

namespace ScaleScanLite.Views;

/// <summary>Hộp thông báo tự đóng sau vài giây nhưng vẫn có nút Xác nhận để đóng sớm.</summary>
public partial class NotifyWindow : Window
{
    public const int AutoCloseSeconds = 3;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining = AutoCloseSeconds;

    internal NotifyWindow(NoticeLevel level, string message)
    {
        InitializeComponent();

        var (icon, title, color) = level switch
        {
            NoticeLevel.Success => ("✓", "Thành công", "#22C55E"),
            NoticeLevel.Warning => ("!", "Cảnh báo", "#F59E0B"),
            NoticeLevel.Error => ("✕", "Lỗi", "#EF4444"),
            _ => ("i", "Thông báo", "#4F6EF7")
        };

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        TopBar.Background = brush;
        IconCircle.Background = brush;
        TxtIcon.Text = icon;
        TxtTitle.Text = title;
        TxtMessage.Text = message;
        UpdateCountdown();

        _timer.Tick += (_, _) =>
        {
            _remaining--;
            if (_remaining <= 0)
            {
                Close();
                return;
            }

            UpdateCountdown();
        };

        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void UpdateCountdown() => TxtCountdown.Text = $"Tự đóng sau {_remaining}s";

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Close();
}
