namespace ScaleScanLite.Core.Models;

/// <summary>Thông tin PO lấy từ SQL (đã gộp dữ liệu đóng gói).</summary>
public sealed record PoInfo(
    string Aufnr,
    string Phcd,
    string Phtx,
    string Pstx,
    double Quantity,
    double NetWeight,
    int PackQty);

public sealed record WeighingRecord(
    long Id,
    string Aufnr,
    string Phcd,
    string Phtx,
    string Pstx,
    double Quantity,
    double Kg,
    DateTime WeighedAt);

public enum SaveResult
{
    /// <summary>PO chưa có trong lịch sử: đã thêm mới.</summary>
    Saved,

    /// <summary>PO đã được cân trước đó: đã cập nhật số kg mới và thời gian mới.</summary>
    Updated,

    /// <summary>Mất kết nối SQL: đã lưu tạm trên máy, sẽ đồng bộ sau.</summary>
    Queued
}

/// <summary>Kết quả lưu một lượt cân; <see cref="PreviousKg"/> có giá trị khi là cập nhật (số kg đã lưu trước đó).</summary>
public sealed record SaveOutcome(SaveResult Result, double? PreviousKg = null);

public sealed record WeightCheckResult(
    double Expected,
    double Actual,
    double Difference,
    double Allowed,
    bool WithinTolerance);

public sealed record WeighingOutcome(
    PoInfo Po,
    double Kg,
    SaveResult Result,
    WeightCheckResult? Check,
    double? PreviousKg = null);
