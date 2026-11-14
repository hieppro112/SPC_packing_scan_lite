using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace ScaleScanLite.Views;

/// <summary>Cửa sổ nhỏ luôn nổi ở góc dưới bên phải: chỉ hiện số kg, mã PO, trạng thái và thông báo khi cân xong.</summary>
public partial class MiniWindow : Window
{
    public MiniWindow() => InitializeComponent();

    public event Action? RestoreRequested;

    public void SnapToBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;
    }

    /// <summary>Lớp phủ "đặt hàng lên cân" hiện ra thì chạy hoạt ảnh hộp rơi; ẩn đi thì dừng cho nhẹ máy.</summary>
    private void PlaceHintOverlay_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var storyboard = (Storyboard)PlaceHintOverlay.Resources["DropBox"];
        if (e.NewValue is true)
            storyboard.Begin(this, true);
        else
            storyboard.Stop(this);
    }

    private void Restore_Click(object sender, RoutedEventArgs e) => RestoreRequested?.Invoke();

    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            RestoreRequested?.Invoke();
            return;
        }

        if (e.OriginalSource is not System.Windows.Controls.Button)
            DragMove();
    }
}
