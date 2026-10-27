namespace ScaleScanLite.Core.Logic;

/// <summary>Số cân được coi là ổn định khi N lần đọc liên tiếp chênh nhau không quá một ngưỡng.</summary>
public sealed class StabilityDetector
{
    private readonly Queue<double> _window = new();
    private readonly int _requiredCount;
    private readonly double _maxSpread;
    private readonly double _minWeight;

    public StabilityDetector(int requiredCount, double maxSpreadKg, double minWeightKg)
    {
        _requiredCount = Math.Max(1, requiredCount);
        _maxSpread = maxSpreadKg;
        _minWeight = minWeightKg;
    }

    public bool Add(double kg, out double stableValue)
    {
        _window.Enqueue(kg);
        while (_window.Count > _requiredCount)
            _window.Dequeue();

        stableValue = kg;
        if (_window.Count < _requiredCount)
            return false;

        double min = double.MaxValue, max = double.MinValue, sum = 0;
        foreach (var v in _window)
        {
            if (v < min) min = v;
            if (v > max) max = v;
            sum += v;
        }

        if (max - min > _maxSpread + 1e-9)
            return false;

        stableValue = Math.Round(sum / _window.Count, 3);
        return stableValue >= _minWeight;
    }

    public void Reset() => _window.Clear();
}
