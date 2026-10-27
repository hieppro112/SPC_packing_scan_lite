using System.Data;
using Microsoft.Data.SqlClient;
using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;

namespace ScaleScanLite.Services.Data;

/// <summary>Tra thông tin PO (kết hợp bảng đóng gói ở F2Database). Giữ nguyên quy tắc ghép mã khách hàng của hệ thống cũ.</summary>
internal sealed class SqlPoRepository : IPoRepository
{
    private const string Query = @"
SELECT TOP (1)
    REQ_HED.AUFNR,
    REQ_HED.PHCD,
    REQ_HED.PHTX,
    REQ_HED.PSTX,
    REQ_HED.GAMNG,
    Packing.NW,
    Packing.Qty
FROM [dbo].[MANUFA_F_PD_DT_REQ_HED] AS REQ_HED
LEFT JOIN [dbo].[MANUFA_F_PD_DT_ORDER_DTL] AS dtl
    ON dtl.VBELN = REQ_HED.KDAUF
LEFT JOIN [F2Database].[dbo].[F2_PackingList_Main] AS Packing
    ON Packing.PoNo LIKE '%' + CASE
        WHEN dtl.RRONYU1 IN ('KJS', 'KJK', 'KJS_TK', 'ERC', 'WRC', 'CRC', 'FJS')
             AND SUBSTRING(dtl.ZGLOBAL_CODE, 7, 1) = '-'
        THEN SUBSTRING(dtl.ZGLOBAL_CODE, 8, LEN(dtl.ZGLOBAL_CODE))
        ELSE dtl.ZGLOBAL_CODE
    END + '%'
WHERE REQ_HED.AUFNR = @id;";

    private readonly SqlConnectionFactory _factory;

    public SqlPoRepository(SqlConnectionFactory factory) => _factory = factory;

    public async Task<PoInfo?> FindAsync(string aufnr, CancellationToken ct)
    {
        try
        {
            await using var connection = _factory.CreatePoConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = new SqlCommand(Query, connection) { CommandTimeout = _factory.CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@id", SqlDbType.NVarChar, 50) { Value = aufnr.Trim() });

            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                return null;

            return new PoInfo(
                Aufnr: Text(reader, "AUFNR"),
                Phcd: Text(reader, "PHCD"),
                Phtx: Text(reader, "PHTX"),
                Pstx: Text(reader, "PSTX"),
                Quantity: Number(reader, "GAMNG"),
                NetWeight: Number(reader, "NW"),
                PackQty: (int)Math.Round(Number(reader, "Qty")));
        }
        catch (Exception ex) when (SqlConnectionFactory.IsConnectivityError(ex))
        {
            throw SqlConnectionFactory.Wrap(ex);
        }
    }

    private static string Text(SqlDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i))?.Trim() ?? "";
    }

    private static double Number(SqlDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? 0 : Convert.ToDouble(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
    }
}
