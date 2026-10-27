using System.Text.RegularExpressions;

namespace ScaleScanLite.Core.Logic;

/// <summary>
/// Rút gọn đường dẫn thiết bị HID của Windows về dạng ổn định "VID_xxxx&amp;PID_yyyy" để nhận ra đúng máy quét
/// dù cắm sang cổng USB khác (phần số thể hiện phía sau của đường dẫn sẽ đổi theo cổng).
/// </summary>
public static class ScannerDeviceId
{
    private static readonly Regex VidRegex = new(@"VID[_&]([0-9A-F]{4,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidRegex = new(@"PID[_&]([0-9A-F]{4,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string FromDevicePath(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
            return "";

        var vid = VidRegex.Match(devicePath);
        var pid = PidRegex.Match(devicePath);
        if (vid.Success && pid.Success)
            return $"VID_{vid.Groups[1].Value.ToUpperInvariant()}&PID_{pid.Groups[1].Value.ToUpperInvariant()}";

        return devicePath.Trim().ToUpperInvariant();
    }

    public static bool Matches(string? configuredId, string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(configuredId) || string.IsNullOrWhiteSpace(devicePath))
            return false;

        return string.Equals(
            FromDevicePath(configuredId),
            FromDevicePath(devicePath),
            StringComparison.OrdinalIgnoreCase);
    }
}
