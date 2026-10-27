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
    Saved,
    Duplicate,
    Queued
}

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
    WeightCheckResult? Check);
