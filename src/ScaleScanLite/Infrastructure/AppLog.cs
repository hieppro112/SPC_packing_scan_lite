using System.Text;

namespace ScaleScanLite.Infrastructure;

/// <summary>Log file xoay vòng: app.log (tối đa 1MB) và 3 bản cũ app.1.log ... app.3.log.</summary>
internal static class AppLog
{
    private const long MaxBytes = 1_000_000;
    private const int Keep = 3;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.Logs, "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : message + Environment.NewLine + ex);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                Rotate();
                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [t{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Ghi log không bao giờ được làm hỏng ứng dụng.
        }
    }

    private static void Rotate()
    {
        var info = new FileInfo(FilePath);
        if (!info.Exists || info.Length < MaxBytes)
            return;

        for (int i = Keep - 1; i >= 1; i--)
        {
            var from = Path.Combine(AppPaths.Logs, $"app.{i}.log");
            var to = Path.Combine(AppPaths.Logs, $"app.{i + 1}.log");
            if (File.Exists(from))
                File.Move(from, to, overwrite: true);
        }

        File.Move(FilePath, Path.Combine(AppPaths.Logs, "app.1.log"), overwrite: true);
    }
}
