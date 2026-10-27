namespace ScaleScanLite.Core.Logic;

public static class PoParser
{
    /// <summary>
    /// Hai định dạng hợp lệ:
    /// a) chuỗi có dấu phẩy, hơn 2 phần: PO là phần tử thứ 2;
    /// b) chuỗi dài từ <paramref name="minPlainLength"/> ký tự: cả chuỗi là PO.
    /// </summary>
    public static bool TryParse(string? code, int minPlainLength, out string po)
    {
        po = "";
        if (string.IsNullOrWhiteSpace(code))
            return false;

        code = code.Trim();
        var parts = code.Split(',');

        if (parts.Length > 2)
        {
            var candidate = parts[1].Trim();
            if (candidate.Length == 0)
                return false;
            po = candidate;
            return true;
        }

        if (code.Length >= minPlainLength)
        {
            po = code;
            return true;
        }

        return false;
    }
}
