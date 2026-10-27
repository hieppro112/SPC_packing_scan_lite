using System.Security.Cryptography;
using System.Text;

namespace ScaleScanLite.Infrastructure;

/// <summary>Mã hóa mật khẩu bằng Windows DPAPI (phạm vi máy) để không lưu mật khẩu dạng chữ thường trong file cấu hình.</summary>
internal static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScaleScanLite.v1");

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain))
            return "";

        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(data);
    }

    public static string Unprotect(string protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64))
            return "";

        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            AppLog.Warn("Không giải mã được mật khẩu đã lưu (có thể cấu hình được chép từ máy khác): " + ex.Message);
            return "";
        }
    }
}
