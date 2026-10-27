using ScaleScanLite.Core.Models;

namespace ScaleScanLite.Core.Abstractions;

/// <summary>Không kết nối được tới nguồn dữ liệu (mạng/SQL). Workflow sẽ lưu tạm thay vì mất dữ liệu.</summary>
public sealed class DataUnavailableException : Exception
{
    public DataUnavailableException(string message, Exception? inner = null) : base(message, inner) { }
}

public sealed record HistoryPage(IReadOnlyList<WeighingRecord> Items, int TotalItems);

public interface IPoRepository
{
    Task<PoInfo?> FindAsync(string aufnr, CancellationToken ct);
}

public interface IHistoryRepository
{
    /// <summary>
    /// Lưu lượt cân của một PO: chưa có thì thêm mới (Saved); đã có thì cập nhật số kg mới và thời gian mới
    /// (Updated, kèm số kg cũ). Ném <see cref="DataUnavailableException"/> khi mất kết nối.
    /// </summary>
    Task<SaveOutcome> SaveAsync(WeighingRecord record, CancellationToken ct);

    Task<HistoryPage> QueryAsync(string? poFilter, bool contains, int page, int pageSize, CancellationToken ct);

    Task<IReadOnlyList<WeighingRecord>> QueryAllAsync(string? poFilter, bool contains, int maxRows, CancellationToken ct);
}

public interface IScaleReader
{
    /// <summary>Bắt đầu gửi lệnh yêu cầu số cân định kỳ.</summary>
    void StartPolling();

    void StopPolling();
}

public interface IErrorLogWriter
{
    void WriteMismatch(WeighingRecord record, PoInfo po, WeightCheckResult check);
}

public interface IPendingQueue
{
    void Enqueue(WeighingRecord record);
}
