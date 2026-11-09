namespace ScaleScanLite.Core.Logic;

public static class WeightSplitter
{
    /// <summary>
    /// Chia tổng số cân cho các PO theo số pcs (PO nhiều pcs nhận nhiều kg hơn), làm tròn 3 chữ số thập phân.
    /// Phần dư do làm tròn dồn vào PO cuối để tổng các phần luôn bằng đúng tổng cân.
    /// Nếu tổng pcs không dương thì chia đều.
    /// </summary>
    public static double[] Split(double totalKg, IReadOnlyList<double> pcs)
    {
        int n = pcs.Count;
        if (n == 0)
            return Array.Empty<double>();

        double sum = 0;
        foreach (var q in pcs)
            sum += Math.Max(0, q);

        var result = new double[n];
        double assigned = 0;
        for (int i = 0; i < n - 1; i++)
        {
            double share = sum > 0 ? Math.Max(0, pcs[i]) / sum : 1.0 / n;
            result[i] = Math.Round(totalKg * share, 3);
            assigned += result[i];
        }

        result[n - 1] = Math.Round(totalKg - assigned, 3);
        return result;
    }
}