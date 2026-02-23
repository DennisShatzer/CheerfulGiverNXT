using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Persists the per-campaign semicolon-separated list of "Radio" fund tokens.
///
/// Table shape expected (very small / admin-managed):
/// dbo.CGFirstTimeFundExclusions
///   - CampaignRecordId INT (PK)
///   - FundTokens NVARCHAR(MAX)
///   - UpdatedAt DATETIME2
///   - UpdatedBy NVARCHAR(200)
/// </summary>
public sealed class SqlRadioFundTokensRepository
{
    private readonly string _connectionString;

    public SqlRadioFundTokensRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<string?> TryGetRawAsync(int campaignRecordId, CancellationToken ct = default)
    {
        if (campaignRecordId <= 0) return null;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await TableExistsAsync(conn, "CGFirstTimeFundExclusions", ct).ConfigureAwait(false))
            return null;

        if (!await ColumnExistsAsync(conn, "CGFirstTimeFundExclusions", "FundTokens", ct).ConfigureAwait(false))
            return null;

        const string sql = @"
SELECT TOP (1) FundTokens
FROM dbo.CGFirstTimeFundExclusions
WHERE CampaignRecordId = @CampaignRecordId;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });

        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var raw = (obj is null || obj is DBNull) ? null : (Convert.ToString(obj) ?? "");
        raw = (raw ?? "").Trim();
        return string.IsNullOrWhiteSpace(raw) ? "" : raw;
    }

    public async Task SaveRawAsync(int campaignRecordId, string rawTokens, string? updatedBy, CancellationToken ct = default)
    {
        if (campaignRecordId <= 0) throw new ArgumentOutOfRangeException(nameof(campaignRecordId));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await TableExistsAsync(conn, "CGFirstTimeFundExclusions", ct).ConfigureAwait(false))
            throw new InvalidOperationException("Table dbo.CGFirstTimeFundExclusions was not found. Run the migration script to enable DB-managed Radio fund tokens.");

        if (!await ColumnExistsAsync(conn, "CGFirstTimeFundExclusions", "FundTokens", ct).ConfigureAwait(false))
            throw new InvalidOperationException("dbo.CGFirstTimeFundExclusions is not in the expected format (missing column FundTokens). Run the migration script to enable DB-managed Radio fund tokens.");

        const string sql = @"
IF EXISTS (SELECT 1 FROM dbo.CGFirstTimeFundExclusions WHERE CampaignRecordId = @CampaignRecordId)
BEGIN
    UPDATE dbo.CGFirstTimeFundExclusions
    SET FundTokens = @FundTokens,
        UpdatedAt = SYSUTCDATETIME(),
        UpdatedBy = @UpdatedBy
    WHERE CampaignRecordId = @CampaignRecordId;
END
ELSE
BEGIN
    INSERT INTO dbo.CGFirstTimeFundExclusions (CampaignRecordId, FundTokens, UpdatedAt, UpdatedBy)
    VALUES (@CampaignRecordId, @FundTokens, SYSUTCDATETIME(), @UpdatedBy);
END";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });
        cmd.Parameters.Add(new SqlParameter("@FundTokens", SqlDbType.NVarChar, -1) { Value = (rawTokens ?? "").Trim() });
        cmd.Parameters.Add(new SqlParameter("@UpdatedBy", SqlDbType.NVarChar, 200)
        { Value = (object?)((updatedBy ?? "").Trim() == "" ? null : updatedBy!.Trim()) ?? DBNull.Value });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<bool> TableExistsAsync(SqlConnection conn, string tableName, CancellationToken ct)
    {
        const string sql = @"
SELECT 1
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = 'dbo' AND t.name = @Table;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Table", SqlDbType.NVarChar, 128) { Value = tableName });

        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return obj is not null;
    }

    private static async Task<bool> ColumnExistsAsync(SqlConnection conn, string tableName, string columnName, CancellationToken ct)
    {
        const string sql = @"
SELECT 1
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = 'dbo' AND t.name = @Table AND c.name = @Col;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Table", SqlDbType.NVarChar, 128) { Value = tableName });
        cmd.Parameters.Add(new SqlParameter("@Col", SqlDbType.NVarChar, 128) { Value = columnName });

        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return obj is not null;
    }
}
