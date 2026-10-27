using System.Text.Json;
using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;

namespace ScaleScanLite.Services;

/// <summary>Ghi mỗi lần cân lệch khối lượng chuẩn thành một file JSON trong thư mục cấu hình.</summary>
internal sealed class ErrorLogWriter : IErrorLogWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SettingsService _settings;

    public ErrorLogWriter(SettingsService settings) => _settings = settings;

    public void WriteMismatch(WeighingRecord record, PoInfo po, WeightCheckResult check)
    {
        var folder = _settings.ResolveErrorLogFolder();
        Directory.CreateDirectory(folder);

        var safePo = string.Concat(po.Aufnr.Where(char.IsLetterOrDigit));
        var file = Path.Combine(folder, $"log_packing_{record.WeighedAt:yyyy-MM-dd-HH-mm-ss-fff}_{safePo}.json");

        var payload = new
        {
            record.Aufnr,
            record.Phcd,
            record.Phtx,
            record.Pstx,
            record.Quantity,
            Kg = record.Kg,
            record.WeighedAt,
            Expected = check.Expected,
            Difference = check.Difference,
            Allowed = check.Allowed,
            po.NetWeight,
            po.PackQty
        };

        File.WriteAllText(file, JsonSerializer.Serialize(payload, JsonOptions));
    }
}
