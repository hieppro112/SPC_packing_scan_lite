using System.Globalization;
using System.Text.RegularExpressions;

namespace ScaleScanLite.Core.Logic;

public static class ScaleLineParser
{
    private static readonly Regex NumberRegex = new(@"[-+]?\d+(?:[.,]\d+)?", RegexOptions.Compiled);

    /// <summary>Lấy số đầu tiên trong dòng trả về của cân, ví dụ "ST,GS,  12.345 kg".</summary>
    public static bool TryParse(string? line, out double kg)
    {
        kg = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var m = NumberRegex.Match(line);
        if (!m.Success)
            return false;

        return double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out kg);
    }
}
