using System.Windows;
using ScaleScanLite.Core.Workflow;
using ScaleScanLite.Views;

namespace ScaleScanLite.Services;

internal interface INoticeService
{
    void Show(NoticeLevel level, string message);
}

/// <summary>Hiện hộp thông báo tự đóng; chỉ giữ một hộp tại một thời điểm (hộp mới thay hộp cũ).</summary>
internal sealed class NoticeWindowService : INoticeService
{
    private NotifyWindow? _current;

    public void Show(NoticeLevel level, string message)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(level, message));
            return;
        }

        _current?.Close();

        var window = new NotifyWindow(level, message);
        var main = Application.Current.MainWindow;
        if (main is { IsVisible: true })
        {
            window.Owner = main;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, window))
                _current = null;
        };

        _current = window;
        window.Show();
    }
}
