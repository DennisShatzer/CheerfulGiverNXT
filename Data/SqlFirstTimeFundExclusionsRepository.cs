using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

public sealed class FirstTimeFundExclusionRow
{
    public string FundName { get; set; } = "";
    public bool IsActive { get; set; }
    public int? SortOrder { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public sealed class SqlFirstTimeFundExclusionsRepository
{
    private readonly string _connectionString;

    public SqlFirstTimeFundExclusionsRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<FirstTimeFundExclusionRow[]> ListAsync(int campaignRecordId, CancellationToken ct = default)
    {
        if (campaignRecordId <= 0) return Array.Empty<FirstTimeFundExclusionRow>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var table = await TableExistsAsync(conn, "CGFirstTimeGiverFundExclusions", ct).ConfigureAwait(false)
            ? "CGFirstTimeGiverFundExclusions"
            : "CGFirstTimeFundExclusions";

        if (!await TableExistsAsync(conn, table, ct).ConfigureAwait(false))
            return Array.Empty<FirstTimeFundExclusionRow>();

        var sql = $@"
SELECT FundName, IsActive, SortOrder, CreatedAt
FROM dbo.[{table}]
WHERE CampaignRecordId = @CampaignRecordId
ORDER BY SortOrder, FundName;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });

        var list = new List<FirstTimeFundExclusionRow>();

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var fund = rdr.IsDBNull(0) ? "" : (rdr.GetString(0) ?? "");
            var active = !rdr.IsDBNull(1) && rdr.GetBoolean(1);
            int? sort = rdr.IsDBNull(2) ? null : rdr.GetInt32(2);
            DateTime? created = rdr.IsDBNull(3) ? null : rdr.GetDateTime(3);

            list.Add(new FirstTimeFundExclusionRow
            {
                FundName = (fund ?? "").Trim(),
                IsActive = active,
                SortOrder = sort,
                CreatedAt = created
            });
        }

        return list.ToArray();
    }

    /// <summary>
    /// Replaces all exclusions for the given campaign in a single transaction.
    /// Keeps the implementation intentionally simple (DELETE + INSERT) because the table is small.
    /// </summary>
    public async Task ReplaceAllAsync(int campaignRecordId, IEnumerable<FirstTimeFundExclusionRow> rows, CancellationToken ct = default)
    {
        if (campaignRecordId <= 0) throw new ArgumentOutOfRangeException(nameof(campaignRecordId));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var table = await TableExistsAsync(conn, "CGFirstTimeGiverFundExclusions", ct).ConfigureAwait(false)
            ? "CGFirstTimeGiverFundExclusions"
            : "CGFirstTimeFundExclusions";

        if (!await TableExistsAsync(conn, table, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Table dbo.{table} was not found in the database.");

        await using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            // Remove existing rows for this campaign.
            {
                var del = $"DELETE FROM dbo.[{table}] WHERE CampaignRecordId = @CampaignRecordId;";
                await using var cmd = new SqlCommand(del, conn, tx);
                cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            // Insert new rows (ignore blank fund names).
            var ins = $@"
INSERT INTO dbo.[{table}]
    (CampaignRecordId, FundName, IsActive, SortOrder, CreatedAt)
VALUES
    (@CampaignRecordId, @FundName, @IsActive, @SortOrder, SYSUTCDATETIME());";

            foreach (var r in rows)
            {
                var fund = (r.FundName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(fund))
                    continue;

                await using var cmd = new SqlCommand(ins, conn, tx);
                cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });
                cmd.Parameters.Add(new SqlParameter("@FundName", SqlDbType.NVarChar, 200) { Value = fund });
                cmd.Parameters.Add(new SqlParameter("@IsActive", SqlDbType.Bit) { Value = r.IsActive });
                cmd.Parameters.Add(new SqlParameter("@SortOrder", SqlDbType.Int) { Value = (object?)r.SortOrder ?? DBNull.Value });

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string?> TryGetCampaignNameAsync(int campaignRecordId, CancellationToken ct = default)
    {
        if (campaignRecordId <= 0) return null;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await TableExistsAsync(conn, "CGCampaigns", ct).ConfigureAwait(false))
            return null;

        const string sql = @"
SELECT TOP (1) CampaignName
FROM dbo.CGCampaigns
WHERE CampaignRecordId = @Id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = campaignRecordId });

        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var name = (obj is null || obj is DBNull) ? "" : (Convert.ToString(obj) ?? "");
        name = name.Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
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
