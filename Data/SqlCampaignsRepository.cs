using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

public sealed class CampaignRow
{
    public int CampaignRecordId { get; set; }
    public string CampaignName { get; set; } = "";
    public DateTime StartLocal { get; set; }
    public DateTime EndLocalExclusive { get; set; }
    public string TimeZoneId { get; set; } = "";
    public decimal? GoalAmount { get; set; }
    public int? GoalFirstTimeGivers { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
}

public sealed class SqlCampaignsRepository
{
    private readonly string _connectionString;

    public SqlCampaignsRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<CampaignRow[]> ListAsync(CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await TableExistsAsync(conn, "CGCampaigns", ct).ConfigureAwait(false))
            return Array.Empty<CampaignRow>();

        const string sql = @"
SELECT
    CampaignRecordId,
    CampaignName,
    StartLocal,
    EndLocalExclusive,
    TimeZoneId,
    GoalAmount,
    GoalFirstTimeGivers,
    IsActive,
    CreatedAt,
    CreatedBy,
    UpdatedAt,
    UpdatedBy
FROM dbo.CGCampaigns
ORDER BY StartLocal DESC, CampaignRecordId DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        var list = new List<CampaignRow>();

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new CampaignRow
            {
                CampaignRecordId = rdr.GetInt32(0),
                CampaignName = (rdr.IsDBNull(1) ? "" : (rdr.GetString(1) ?? "")).Trim(),
                StartLocal = rdr.GetDateTime(2),
                EndLocalExclusive = rdr.GetDateTime(3),
                TimeZoneId = (rdr.IsDBNull(4) ? "" : (rdr.GetString(4) ?? "")).Trim(),
                GoalAmount = rdr.IsDBNull(5) ? null : rdr.GetDecimal(5),
                GoalFirstTimeGivers = rdr.IsDBNull(6) ? null : rdr.GetInt32(6),
                IsActive = !rdr.IsDBNull(7) && rdr.GetBoolean(7),
                CreatedAt = rdr.GetDateTime(8),
                CreatedBy = rdr.IsDBNull(9) ? null : rdr.GetString(9),
                UpdatedAt = rdr.GetDateTime(10),
                UpdatedBy = rdr.IsDBNull(11) ? null : rdr.GetString(11)
            });
        }

        return list.ToArray();
    }

    public async Task SaveAsync(IEnumerable<CampaignRow> rows, IEnumerable<int> deleteIds, string? username, CancellationToken ct = default)
    {
        username ??= Environment.UserName;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await TableExistsAsync(conn, "CGCampaigns", ct).ConfigureAwait(false))
            throw new InvalidOperationException("Table dbo.CGCampaigns was not found in the database.");

        await using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            // Deletes first (if any)
            const string delSql = "DELETE FROM dbo.CGCampaigns WHERE CampaignRecordId = @Id;";
            foreach (var id in deleteIds)
            {
                if (id <= 0) continue;
                await using var cmd = new SqlCommand(delSql, conn, tx);
                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = id });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Upserts
            const string insSql = @"
INSERT INTO dbo.CGCampaigns
    (CampaignName, StartLocal, EndLocalExclusive, TimeZoneId, GoalAmount, GoalFirstTimeGivers, IsActive, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
VALUES
    (@CampaignName, @StartLocal, @EndLocalExclusive, @TimeZoneId, @GoalAmount, @GoalFirstTimeGivers, @IsActive, SYSUTCDATETIME(), @User, SYSUTCDATETIME(), @User);
SELECT CAST(SCOPE_IDENTITY() AS int);";

            const string updSql = @"
UPDATE dbo.CGCampaigns
SET
    CampaignName = @CampaignName,
    StartLocal = @StartLocal,
    EndLocalExclusive = @EndLocalExclusive,
    TimeZoneId = @TimeZoneId,
    GoalAmount = @GoalAmount,
    GoalFirstTimeGivers = @GoalFirstTimeGivers,
    IsActive = @IsActive,
    UpdatedAt = SYSUTCDATETIME(),
    UpdatedBy = @User
WHERE CampaignRecordId = @CampaignRecordId;";

            foreach (var r in rows)
            {
                var name = (r.CampaignName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue; // skip blanks

                var tz = (r.TimeZoneId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(tz))
                    tz = "America/New_York";

                if (r.CampaignRecordId <= 0)
                {
                    await using var cmd = new SqlCommand(insSql, conn, tx);
                    AddParams(cmd, r, name, tz, username, includeId: false);
                    var newIdObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (newIdObj is not null && newIdObj is not DBNull)
                        r.CampaignRecordId = Convert.ToInt32(newIdObj);
                }
                else
                {
                    await using var cmd = new SqlCommand(updSql, conn, tx);
                    AddParams(cmd, r, name, tz, username, includeId: true);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private static void AddParams(SqlCommand cmd, CampaignRow r, string name, string tz, string user, bool includeId)
    {
        if (includeId)
            cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = r.CampaignRecordId });

        cmd.Parameters.Add(new SqlParameter("@CampaignName", SqlDbType.NVarChar, 200) { Value = name });
        cmd.Parameters.Add(new SqlParameter("@StartLocal", SqlDbType.DateTime2) { Value = r.StartLocal });
        cmd.Parameters.Add(new SqlParameter("@EndLocalExclusive", SqlDbType.DateTime2) { Value = r.EndLocalExclusive });
        cmd.Parameters.Add(new SqlParameter("@TimeZoneId", SqlDbType.NVarChar, 100) { Value = tz });
        cmd.Parameters.Add(new SqlParameter("@GoalAmount", SqlDbType.Decimal)
        {
            Precision = 18,
            Scale = 2,
            Value = (object?)r.GoalAmount ?? DBNull.Value
        });
        cmd.Parameters.Add(new SqlParameter("@GoalFirstTimeGivers", SqlDbType.Int) { Value = (object?)r.GoalFirstTimeGivers ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@IsActive", SqlDbType.Bit) { Value = r.IsActive });
        cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 200) { Value = (object?)user ?? DBNull.Value });
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
}
