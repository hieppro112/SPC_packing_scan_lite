using System.Data;
using Microsoft.Data.SqlClient;
using ScaleScanLite.Core.Abstractions;
using ScaleScanLite.Core.Models;

namespace ScaleScanLite.Services.Data;

internal sealed class SqlHistoryRepository : IHistoryRepository
{
    private const string InsertSql = @"
INSERT INTO [KVH].[dbo].[PackingScaleHistory] (AUFNR, PHCD, PHTX, PSTX, GAMNG, kg, UPDDT)
SELECT @AUFNR, @PHCD, @PHTX, @PSTX, @GAMNG, @kg, @UPDDT
WHERE NOT EXISTS (
    SELECT 1 FROM [KVH].[dbo].[PackingScaleHistory] WITH (UPDLOCK, HOLDLOCK) WHERE AUFNR = @AUFNR
);";

    private const string Columns = "Id, AUFNR, PHCD, PHTX, PSTX, GAMNG, kg, UPDDT";

    private readonly SqlConnectionFactory _factory;

    public SqlHistoryRepository(SqlConnectionFactory factory) => _factory = factory;

    public async Task<SaveResult> InsertAsync(WeighingRecord record, CancellationToken ct)
    {
        try
        {
            await using var connection = _factory.CreateHistoryConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = new SqlCommand(InsertSql, connection) { CommandTimeout = _factory.CommandTimeoutSeconds };
            command.Parameters.Add(new SqlParameter("@AUFNR", SqlDbType.NVarChar, 50) { Value = record.Aufnr });
            command.Parameters.Add(new SqlParameter("@PHCD", SqlDbType.NVarChar, 100) { Value = record.Phcd });
            command.Parameters.Add(new SqlParameter("@PHTX", SqlDbType.NVarChar, 200) { Value = record.Phtx });
            command.Parameters.Add(new SqlParameter("@PSTX", SqlDbType.NVarChar, 200) { Value = record.Pstx });
            command.Parameters.Add(new SqlParameter("@GAMNG", SqlDbType.Float) { Value = record.Quantity });
            command.Parameters.Add(new SqlParameter("@kg", SqlDbType.Float) { Value = record.Kg });
            command.Parameters.Add(new SqlParameter("@UPDDT", SqlDbType.DateTime) { Value = record.WeighedAt });

            int rows = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return rows > 0 ? SaveResult.Saved : SaveResult.Duplicate;
        }
        catch (Exception ex) when (SqlConnectionFactory.IsConnectivityError(ex))
        {
            throw SqlConnectionFactory.Wrap(ex);
        }
    }

    public async Task<HistoryPage> QueryAsync(string? poFilter, bool contains, int page, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Max(1, pageSize);
        page = Math.Max(1, page);
        var (where, pattern) = BuildFilter(poFilter, contains);

        var sql = $@"
SELECT {Columns}, COUNT(*) OVER() AS TotalCount
FROM [KVH].[dbo].[PackingScaleHistory]
{where}
ORDER BY Id DESC
OFFSET @offset ROWS FETCH NEXT @take ROWS ONLY;";

        try
        {
            await using var connection = _factory.CreateHistoryConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var items = new List<WeighingRecord>();
            int total = 0;

            await using (var command = new SqlCommand(sql, connection) { CommandTimeout = _factory.CommandTimeoutSeconds })
            {
                AddFilterParameter(command, pattern);
                command.Parameters.Add(new SqlParameter("@offset", SqlDbType.Int) { Value = (page - 1) * pageSize });
                command.Parameters.Add(new SqlParameter("@take", SqlDbType.Int) { Value = pageSize });

                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    items.Add(Map(reader));
                    total = Convert.ToInt32(reader["TotalCount"]);
                }
            }

            // Trang vượt quá số dòng (ví dụ vừa xóa/lọc): không có dòng nào nên COUNT(*) OVER() không trả về → đếm riêng.
            if (items.Count == 0 && page > 1)
            {
                await using var countCommand = new SqlCommand(
                    $"SELECT COUNT(*) FROM [KVH].[dbo].[PackingScaleHistory] {where};", connection)
                { CommandTimeout = _factory.CommandTimeoutSeconds };
                AddFilterParameter(countCommand, pattern);
                total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            return new HistoryPage(items, total);
        }
        catch (Exception ex) when (SqlConnectionFactory.IsConnectivityError(ex))
        {
            throw SqlConnectionFactory.Wrap(ex);
        }
    }

    public async Task<IReadOnlyList<WeighingRecord>> QueryAllAsync(string? poFilter, bool contains, int maxRows, CancellationToken ct)
    {
        var (where, pattern) = BuildFilter(poFilter, contains);
        var sql = $@"
SELECT TOP (@max) {Columns}
FROM [KVH].[dbo].[PackingScaleHistory]
{where}
ORDER BY Id DESC;";

        try
        {
            await using var connection = _factory.CreateHistoryConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var command = new SqlCommand(sql, connection) { CommandTimeout = Math.Max(60, _factory.CommandTimeoutSeconds) };
            AddFilterParameter(command, pattern);
            command.Parameters.Add(new SqlParameter("@max", SqlDbType.Int) { Value = Math.Max(1, maxRows) });

            var items = new List<WeighingRecord>();
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                items.Add(Map(reader));
            return items;
        }
        catch (Exception ex) when (SqlConnectionFactory.IsConnectivityError(ex))
        {
            throw SqlConnectionFactory.Wrap(ex);
        }
    }

    private static (string Where, string? Pattern) BuildFilter(string? poFilter, bool contains)
    {
        if (string.IsNullOrWhiteSpace(poFilter))
            return ("", null);

        var escaped = EscapeLike(poFilter.Trim());
        var pattern = (contains ? "%" : "") + escaped + "%";
        return ("WHERE AUFNR LIKE @pattern ESCAPE '\\'", pattern);
    }

    private static void AddFilterParameter(SqlCommand command, string? pattern)
    {
        if (pattern != null)
            command.Parameters.Add(new SqlParameter("@pattern", SqlDbType.NVarChar, 120) { Value = pattern });
    }

    /// <summary>Thoát các ký tự đặc biệt của LIKE để người dùng gõ "%" hay "_" không làm sai kết quả tìm kiếm.</summary>
    internal static string EscapeLike(string value)
        => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");

    private static WeighingRecord Map(SqlDataReader r) => new(
        Id: Convert.ToInt64(r["Id"]),
        Aufnr: Convert.ToString(r["AUFNR"])?.Trim() ?? "",
        Phcd: Convert.ToString(r["PHCD"])?.Trim() ?? "",
        Phtx: Convert.ToString(r["PHTX"])?.Trim() ?? "",
        Pstx: Convert.ToString(r["PSTX"])?.Trim() ?? "",
        Quantity: r["GAMNG"] is DBNull ? 0 : Convert.ToDouble(r["GAMNG"]),
        Kg: r["kg"] is DBNull ? 0 : Math.Round(Convert.ToDouble(r["kg"]), 3),
        WeighedAt: r["UPDDT"] is DBNull ? DateTime.MinValue : Convert.ToDateTime(r["UPDDT"]));
}
