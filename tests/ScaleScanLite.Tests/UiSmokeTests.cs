using System.Windows;
using System.Windows.Controls;
using ScaleScanLite.Core.Workflow;
using ScaleScanLite.Services;
using ScaleScanLite.Services.Input;
using ScaleScanLite.Views;

namespace ScaleScanLite.Tests;

/// <summary>Nạp thật các cửa sổ XAML để chắc không có lỗi cú pháp/tài nguyên (không hiển thị lên màn hình).</summary>
public class UiSmokeTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(30000), "Quá thời gian chạy test giao diện");
        if (error != null)
            throw new Exception("Lỗi trong test giao diện: " + error, error);
    }

    [Fact]
    public void Settings_mini_and_notify_windows_load_with_the_real_theme()
    {
        RunSta(() =>
        {
            var app = new ScaleScanLite.App();
            app.InitializeComponent();   // nạp Theme.xaml và các converter của App.xaml

            var settings = new SettingsService();
            using var scanner = new RawInputScanner();

            var settingsWindow = new SettingsWindow(settings, scanner);
            Assert.IsType<SettingsForm>(settingsWindow.DataContext);
            Assert.NotNull(settingsWindow.FindName("LearnBox"));
            Assert.NotNull(settingsWindow.FindName("SaveButton"));
            Assert.False(((Border)settingsWindow.FindName("LearnPanel")).IsVisible);

            var mini = new MiniWindow();
            Assert.Equal(284, mini.Width);

            foreach (var level in Enum.GetValues<NoticeLevel>())
            {
                var notify = new NotifyWindow(level, "Thông báo thử");
                Assert.NotNull(notify.FindName("BtnOk"));
            }

            app.Shutdown();
        });
    }
}
