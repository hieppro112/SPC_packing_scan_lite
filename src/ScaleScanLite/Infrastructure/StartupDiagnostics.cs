using System.Runtime.InteropServices;

namespace ScaleScanLite.Infrastructure;

/// <summary>Kiểm tra môi trường lúc khởi động và ghi vào log; trả về danh sách vấn đề để báo cho người dùng.</summary>
internal static class StartupDiagnostics
{
    public static IReadOnlyList<string> Run()
    {
        var problems = new List<string>();

        AppLog.Info("=== Khởi động Scale Scan Lite " + typeof(StartupDiagnostics).Assembly.GetName().Version + " ===");
        AppLog.Info($"OS: {RuntimeInformation.OSDescription} | Process: {RuntimeInformation.ProcessArchitecture} | {RuntimeInformation.FrameworkDescription}");
        AppLog.Info("BaseDirectory: " + AppContext.BaseDirectory);
        AppLog.Info("Thư mục dữ liệu: " + AppPaths.Root);

        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var probe = Path.Combine(AppPaths.Root, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            var msg = $"Không ghi được vào thư mục dữ liệu {AppPaths.Root}: {ex.Message}";
            AppLog.Error(msg);
            problems.Add(msg);
        }

        return problems;
    }
}
