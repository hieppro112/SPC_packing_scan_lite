using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Core.Settings;
using ScaleScanLite.Services.Scale;

namespace ScaleScanLite.Services.Demo;

/// <summary>Dữ liệu giả lập để chạy thử toàn bộ ứng dụng khi không có máy quét, cân hay SQL Server.</summary>
internal static class DemoData
{
    // NW 30 / Qty 12 = 2.5 kg mỗi sản phẩm; số lượng 5 => khối lượng chuẩn 12.5 kg
    public const double Quantity = 5;
    public const double NetWeight = 30;
    public const int PackQty = 12;
    public const double ExpectedKg = 12.5;

    public static readonly string[] Codes =
    {
        "A,520006656409,DEMO",
        "520006700001",
        "B,520006700002,DEMO,X",
        "520006700003"
    };

    public static PoInfo CreatePo(string aufnr)
        => new(aufnr, "P0003983", "PJXB4-60-0", "PJXB", Quantity, NetWeight, PackQty);
}

/// <summary>Chế độ chạy thử: định kỳ "quét" lần lượt các mã mẫu như có người dùng máy quét.</summary>
internal sealed class DemoCodeFeeder : IDisposable
{
    private readonly Timer _timer;
    private readonly TimeSpan _firstDelay;
    private readonly TimeSpan _period;
    private int _index;

    public DemoCodeFeeder(TimeSpan? firstDelay = null, TimeSpan? period = null)
    {
        _firstDelay = firstDelay ?? TimeSpan.FromSeconds(3);
        _period = period ?? TimeSpan.FromSeconds(15);
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<string>? CodeScanned;

    public void Start() => _timer.Change(_firstDelay, _period);

    private void Tick()
    {
        var code = DemoData.Codes[_index++ % DemoData.Codes.Length];
        CodeScanned?.Invoke(code);
    }

    public void Dispose() => _timer.Dispose();
}

internal sealed class DemoPoRepository : IPoRepository
{
    public async Task<PoInfo?> FindAsync(string aufnr, CancellationToken ct)
    {
        await Task.Delay(150, ct).ConfigureAwait(false);
        return aufnr.StartsWith("0000", StringComparison.Ordinal) ? null : DemoData.CreatePo(aufnr);
    }
}

internal sealed class InMemoryHistoryRepository : IHistoryRepository
{
    private readonly object _lock = new();
    private readonly List<WeighingRecord> _rows = new();
    private long _nextId = 1;

    public InMemoryHistoryRepository()
    {
        var rng = new Random(7);
        for (int i = 0; i < 37; i++)
        {
            _rows.Add(new WeighingRecord(_nextId++, $"5200060{i:00000}", "P0003983", "PJXB4-60-0", "PJXB", 5,
                Math.Round(12.5 + (rng.NextDouble() - 0.5) * 0.04, 3), DateTime.Now.AddMinutes(-(37 - i) * 6)));
        }
    }

    public async Task<SaveResult> InsertAsync(WeighingRecord record, CancellationToken ct)
    {
        await Task.Delay(120, ct).ConfigureAwait(false);
        lock (_lock)
        {
            if (_rows.Any(r => r.Aufnr == record.Aufnr))
                return SaveResult.Duplicate;

            _rows.Add(record with { Id = _nextId++ });
            return SaveResult.Saved;
        }
    }

    public Task<HistoryPage> QueryAsync(string? poFilter, bool contains, int page, int pageSize, CancellationToken ct)
    {
        lock (_lock)
        {
            var filtered = Filter(poFilter, contains).ToList();
            var items = filtered.Skip((Math.Max(1, page) - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new HistoryPage(items, filtered.Count));
        }
    }

    public Task<IReadOnlyList<WeighingRecord>> QueryAllAsync(string? poFilter, bool contains, int maxRows, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult<IReadOnlyList<WeighingRecord>>(Filter(poFilter, contains).Take(maxRows).ToList());
    }

    private IEnumerable<WeighingRecord> Filter(string? poFilter, bool contains)
    {
        IEnumerable<WeighingRecord> q = _rows.OrderByDescending(r => r.Id);
        if (!string.IsNullOrWhiteSpace(poFilter))
        {
            var f = poFilter.Trim();
            q = q.Where(r => contains
                ? r.Aufnr.Contains(f, StringComparison.OrdinalIgnoreCase)
                : r.Aufnr.StartsWith(f, StringComparison.OrdinalIgnoreCase));
        }

        return q;
    }
}

/// <summary>Cân giả: mỗi lệnh yêu cầu trả về một số, tăng dần rồi ổn định; thỉnh thoảng cố ý lệch để thấy cảnh báo.</summary>
internal sealed class FakeScalePort : IScalePort
{
    private readonly Random _rng = new();
    private readonly System.Threading.Channels.Channel<string> _lines = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private DateTime _lastSend = DateTime.MinValue;
    private int _step;
    private double _target = DemoData.ExpectedKg;

    public FakeScalePort() => _ = Task.Run(PumpAsync);

    /// <summary>Phát từng dòng theo đúng thứ tự gửi, trễ ~40ms giống cân thật.</summary>
    private async Task PumpAsync()
    {
        await foreach (var line in _lines.Reader.ReadAllAsync())
        {
            await Task.Delay(40).ConfigureAwait(false);
            LineReceived?.Invoke(line);
        }
    }

    public event Action<string>? LineReceived;

    public event Action<bool, string>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public void Start(ScaleSettings settings)
    {
        IsConnected = true;
        ConnectionChanged?.Invoke(true, "Cân giả lập");
    }

    public void Stop()
    {
        IsConnected = false;
        ConnectionChanged?.Invoke(false, "Đã đóng cân giả lập");
    }

    public bool Send(string command)
    {
        if (!IsConnected)
            return false;

        lock (this)
        {
            if (DateTime.Now - _lastSend > TimeSpan.FromSeconds(3))
            {
                _step = 0;
                _target = _rng.NextDouble() < 0.8 ? DemoData.ExpectedKg : DemoData.ExpectedKg + 1.3;
            }

            _lastSend = DateTime.Now;
            double value = _step < 4
                ? _target * _step / 4.0
                : _target + (_rng.NextDouble() - 0.5) * 0.002;
            _step++;

            _lines.Writer.TryWrite(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"ST,GS,  {value:0.000} kg"));
        }

        return true;
    }

    public void Dispose()
    {
        IsConnected = false;
        _lines.Writer.TryComplete();
    }
}
