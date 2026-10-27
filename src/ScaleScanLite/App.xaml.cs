using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Workflow;
using ScaleScanLite.Infrastructure;
using ScaleScanLite.Services;
using ScaleScanLite.Services.Data;
using ScaleScanLite.Services.Demo;
using ScaleScanLite.Services.Input;
using ScaleScanLite.Services.Scale;
using ScaleScanLite.ViewModels;
using ScaleScanLite.Views;

namespace ScaleScanLite;

public partial class App : Application
{
    private const string MutexName = @"Global\ScaleScanLite.SingleInstance";

    private Mutex? _singleInstance;
    private ServiceProvider? _services;
    private bool _selfTest;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool selfTest = e.Args.Any(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase));
        _selfTest = selfTest;
        RegisterGlobalHandlers();

        bool demo = e.Args.Any(a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase)) || selfTest;

        if (!selfTest && !AcquireSingleInstance())
        {
            MessageBox.Show("Scale Scan Lite đang chạy trên máy này (có thể đang thu nhỏ ở góc màn hình hoặc nằm trong Task Manager).",
                "Scale Scan Lite", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        var problems = StartupDiagnostics.Run();

        var settings = new SettingsService { DemoOverride = demo };
        settings.Load();
        _services = BuildServices(settings);

        var main = new MainWindow(
            _services.GetRequiredService<MainViewModel>(),
            settings,
            _services.GetRequiredService<RawInputScanner>());
        MainWindow = main;
        main.Show();

        if (problems.Count > 0 && !selfTest)
        {
            MessageBox.Show(main,
                "Phát hiện vấn đề với môi trường:\n\n• " + string.Join("\n• ", problems) +
                "\n\nỨng dụng vẫn mở nhưng một số chức năng có thể không chạy. Chi tiết: " + AppLog.FilePath,
                "Scale Scan Lite", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (selfTest)
            ScheduleSelfTest(e.Args);
    }

    private static ServiceProvider BuildServices(SettingsService settings)
    {
        var services = new ServiceCollection();
        services.AddSingleton(settings);
        services.AddSingleton<SqlConnectionFactory>();

        if (settings.IsDemo)
        {
            services.AddSingleton<IPoRepository, DemoPoRepository>();
            services.AddSingleton<IHistoryRepository, InMemoryHistoryRepository>();
            services.AddSingleton<IScalePort, FakeScalePort>();
            services.AddSingleton(_ => new DemoCodeFeeder());
            services.AddSingleton(sp => new PendingQueue(
                sp.GetRequiredService<IHistoryRepository>(),
                Path.Combine(AppPaths.Root, "pending-demo.jsonl")));
        }
        else
        {
            services.AddSingleton<IPoRepository, SqlPoRepository>();
            services.AddSingleton<IHistoryRepository, SqlHistoryRepository>();
            services.AddSingleton<IScalePort, SerialScalePort>();
            services.AddSingleton(sp => new PendingQueue(sp.GetRequiredService<IHistoryRepository>()));
        }

        services.AddSingleton<IPendingQueue>(sp => sp.GetRequiredService<PendingQueue>());
        services.AddSingleton<ScaleService>();
        services.AddSingleton<IScaleReader>(sp => sp.GetRequiredService<ScaleService>());
        services.AddSingleton<IErrorLogWriter, ErrorLogWriter>();
        services.AddSingleton<INoticeService, NoticeWindowService>();
        services.AddSingleton(_ => new RawInputScanner());
        services.AddSingleton(sp => new WeighingWorkflow(
            sp.GetRequiredService<IPoRepository>(),
            sp.GetRequiredService<IHistoryRepository>(),
            sp.GetRequiredService<IScaleReader>(),
            sp.GetRequiredService<IErrorLogWriter>(),
            sp.GetRequiredService<IPendingQueue>(),
            () => settings.Current.Weighing,
            null,
            AppLog.Info));
        services.AddSingleton<MainViewModel>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// --selftest[=giây]: chạy ở chế độ giả lập vài giây rồi tự thoát; mã thoát 0 nếu mã quét giả lập đi hết quy trình
    /// (tra PO, chờ cân, lưu) và cân, SQL giả lập đều hoạt động.
    /// </summary>
    private void ScheduleSelfTest(string[] args)
    {
        int seconds = 25;
        var arg = args.FirstOrDefault(a => a.StartsWith("--selftest=", StringComparison.OrdinalIgnoreCase));
        if (arg != null && int.TryParse(arg["--selftest=".Length..], out var parsed))
            seconds = Math.Clamp(parsed, 5, 300);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var vm = _services!.GetRequiredService<MainViewModel>();
            var (ok, report) = vm.SelfTestReport();
            AppLog.Info("SELFTEST " + (ok ? "OK" : "FAIL") + ": " + report);
            Shutdown(ok ? 0 : 2);
        };
        timer.Start();
    }

    private bool AcquireSingleInstance()
    {
        try
        {
            _singleInstance = new Mutex(true, MutexName, out bool createdNew);
            return createdNew;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Không tạo được mutex một-bản-chạy: " + ex.Message);
            return true;
        }
    }

    private void RegisterGlobalHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("Lỗi chưa được xử lý trên luồng giao diện", args.Exception);
            args.Handled = true;

            if (_selfTest)
            {
                // Chế độ tự kiểm tra chạy không người trông: không hiện hộp thoại, thoát với mã lỗi.
                Shutdown(3);
                return;
            }

            var root = args.Exception.GetBaseException();
            MessageBox.Show($"Ứng dụng gặp lỗi:\n{root.GetType().Name}: {root.Message}\n\nChi tiết: {AppLog.FilePath}",
                "Scale Scan Lite", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error($"Lỗi nghiêm trọng (isTerminating={args.IsTerminating})", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Task bị lỗi mà không ai quan sát", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Error("Lỗi khi dọn dẹp lúc thoát", ex);
        }

        AppLog.Info($"=== Thoát (mã {e.ApplicationExitCode}) ===");

        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
