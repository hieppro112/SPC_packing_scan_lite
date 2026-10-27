namespace ScaleScanLite.Infrastructure;

internal static class AppPaths
{
    /// <summary>
    /// Thư mục dữ liệu (cấu hình, log, hàng đợi lưu tạm). Mặc định %LocalAppData%\ScaleScanLite;
    /// đặt biến môi trường SCALESCANLITE_DATA để dùng thư mục khác (chạy thử, bản portable) mà không đụng dữ liệu thật.
    /// </summary>
    public static readonly string Root =
        Environment.GetEnvironmentVariable("SCALESCANLITE_DATA") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScaleScanLite");

    public static string Logs => Path.Combine(Root, "logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string PendingFile => Path.Combine(Root, "pending.jsonl");
    public static string DefaultErrorLogs => Path.Combine(Root, "error-logs");
}
