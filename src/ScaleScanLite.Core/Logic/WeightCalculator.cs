using ScaleScanLite.Core.Models;

namespace ScaleScanLite.Core.Logic;

public static class WeightCalculator
{
    /// <summary>Khối lượng chuẩn = round(NW / Qty, 3) * Quantity. Null nếu thiếu dữ liệu đóng gói.</summary>
    public static double? ExpectedKg(double netWeight, int packQty, double quantity)
    {
        if (packQty <= 0 || netWeight <= 0 || quantity <= 0)
            return null;

        return Math.Round(Math.Round(netWeight / packQty, 3) * quantity, 3);
    }

    /// <summary>Cho phép sai lệch = giá trị lớn hơn giữa ngưỡng tuyệt đối (kg) và ngưỡng phần trăm.</summary>
    public static WeightCheckResult Check(double actual, double expected, double toleranceKg, double tolerancePercent)
    {
        var allowed = Math.Max(toleranceKg, Math.Abs(expected) * tolerancePercent / 100.0);
        var diff = Math.Round(actual - expected, 3);
        return new WeightCheckResult(expected, actual, diff, Math.Round(allowed, 3), Math.Abs(diff) <= allowed + 1e-9);
    }
}
