using Microsoft.Data.SqlClient;
using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Infrastructure;

namespace ScaleScanLite.Services.Data;

internal sealed class SqlConnectionFactory
{
    private readonly SettingsService _settings;

    public SqlConnectionFactory(SettingsService settings) => _settings = settings;

    public int CommandTimeoutSeconds => _settings.Current.Database.TimeoutSeconds;

    public SqlConnection CreatePoConnection()
    {
        var d = _settings.Current.Database;
        return new SqlConnection(Build(d.Server, d.PoDatabase, d.PoUser, SecretProtector.Unprotect(d.PoPasswordProtected), d.TimeoutSeconds));
    }

    public SqlConnection CreateHistoryConnection()
    {
        var d = _settings.Current.Database;
        return new SqlConnection(Build(d.Server, d.HistoryDatabase, d.HistoryUser, SecretProtector.Unprotect(d.HistoryPasswordProtected), d.TimeoutSeconds));
    }

    private static string Build(string server, string database, string user, string password, int timeoutSeconds)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            ConnectTimeout = timeoutSeconds,
            // Máy chủ SQL nội bộ thường dùng chứng chỉ tự ký.
            Encrypt = SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = true,
            ApplicationName = "ScaleScanLite"
        };

        if (string.IsNullOrWhiteSpace(user))
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = user;
            builder.Password = password;
        }

        return builder.ConnectionString;
    }

    /// <summary>Lỗi mạng/máy chủ (không phải lỗi dữ liệu hay phân quyền) → coi là tạm thời, có thể lưu cục bộ và thử lại.</summary>
    public static bool IsConnectivityError(Exception ex)
    {
        if (ex is TimeoutException)
            return true;

        if (ex is SqlException sql)
        {
            if (sql.Number is -2 or 2 or 20 or 40 or 53 or 64 or 121 or 233 or 258 or 1231 or 10053 or 10054 or 10060 or 10061 or 11001 or 4060 or 40613)
                return true;

            foreach (SqlError error in sql.Errors)
            {
                if (error.Number is -2 or 2 or 20 or 40 or 53 or 64 or 121 or 233 or 258 or 10053 or 10054 or 10060 or 10061 or 11001)
                    return true;
            }
        }

        return ex is InvalidOperationException { Message: var m } && m.Contains("pool", StringComparison.OrdinalIgnoreCase);
    }

    public static DataUnavailableException Wrap(Exception ex)
        => new("Không kết nối được SQL Server: " + ex.Message, ex);
}
