using System.Windows;
using System.Windows.Input;

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
