using System.Text.Json;
using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;
using ScaleScanLite.Infrastructure;

namespace ScaleScanLite.Services;

/// <summary>
/// Hàng đợi lưu tạm trên máy (JSON Lines) khi mất kết nối SQL. Một bộ hẹn giờ thử đồng bộ lại định kỳ;
/// bản ghi nào lưu được (hoặc đã tồn tại) thì bị xóa khỏi hàng đợi.
/// </summary>
internal sealed class PendingQueue : IPendingQueue, IDisposable
{
    private readonly object _lock = new();
    private readonly IHistoryRepository _history;
    private readonly string _path;
    private readonly Timer _timer;
    private int _syncing;

    public PendingQueue(IHistoryRepository history, string? path = null, TimeSpan? interval = null)
    {
        _history = history;
        _path = path ?? AppPaths.PendingFile;
        var period = interval ?? TimeSpan.FromSeconds(30);
        _timer = new Timer(_ => _ = SyncAsync(), null, period, period);
    }

    /// <summary>Số bản ghi đã đồng bộ lên SQL thành công trong một lần chạy.</summary>
    public event Action<int>? Synced;

    public int Count
    {
        get { lock (_lock) return ReadAll().Count; }
    }

    public void Enqueue(WeighingRecord record)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine);
        }

        AppLog.Warn($"Đã lưu tạm PO {record.Aufnr} ({record.Kg} kg) vào hàng đợi cục bộ");
    }

    public async Task<int> SyncAsync()
    {
        if (Interlocked.Exchange(ref _syncing, 1) == 1)
            return 0;

        try
        {
            List<WeighingRecord> pending;
            lock (_lock) pending = ReadAll();
            if (pending.Count == 0)
                return 0;

            var remaining = new List<WeighingRecord>();
            int synced = 0;
            bool offline = false;

            foreach (var record in pending)
            {
                if (offline)
                {
                    remaining.Add(record);
                    continue;
                }

                try
                {
                    await _history.SaveAsync(record, CancellationToken.None).ConfigureAwait(false);
                    synced++; // thêm mới hay cập nhật PO đã có đều coi là xong
                }
                catch (DataUnavailableException)
                {
                    offline = true;
                    remaining.Add(record);
                }
                catch (Exception ex)
                {
                    AppLog.Error($"Không đồng bộ được bản ghi PO {record.Aufnr}, giữ lại để thử sau", ex);
                    remaining.Add(record);
                }
            }

            lock (_lock)
            {
                // Có thể có bản ghi mới được thêm vào trong lúc đồng bộ: ghi lại phần còn thừa + phần mới.
                var current = ReadAll();
                var newOnes = current.Skip(pending.Count);
                WriteAll(remaining.Concat(newOnes));
            }

            if (synced > 0)
            {
                AppLog.Info($"Đã đồng bộ {synced} bản ghi lưu tạm lên SQL");
                Synced?.Invoke(synced);
            }

            return synced;
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
        }
    }

    private List<WeighingRecord> ReadAll()
    {
        var list = new List<WeighingRecord>();
        if (!File.Exists(_path))
            return list;

        foreach (var line in File.ReadAllLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<WeighingRecord>(line);
                if (record != null)
                    list.Add(record);
            }
            catch (JsonException ex)
            {
                AppLog.Warn("Bỏ qua dòng hỏng trong hàng đợi lưu tạm: " + ex.Message);
            }
        }

        return list;
    }

    private void WriteAll(IEnumerable<WeighingRecord> records)
    {
        var lines = records.Select(r => JsonSerializer.Serialize(r)).ToList();
        if (lines.Count == 0)
        {
            if (File.Exists(_path))
                File.Delete(_path);
            return;
        }

        File.WriteAllLines(_path, lines);
    }

    public void Dispose() => _timer.Dispose();
}
