using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Reads the current CampaignRecordId from dbo.CGCampaigns.
///
/// dbo.CGCampaigns is the single source of truth for campaign configuration.
/// Selection rules:
/// - Prefer a configured "active" flag on dbo.CGCampaigns (IsActive / IsCurrent / IsDefault / IsSelected / IsOpen).
/// - Otherwise, fall back to the most recent CampaignRecordId (MAX).
/// - If dbo.CGCampaigns doesn't exist or has no rows, returns null.
/// </summary>
public sealed class SqlCampaignContext : ICampaignContext
{
    private readonly string _connectionString;

    // Simple in-memory cache to avoid hitting SQL repeatedly while the window is open.
    private int? _cached;
    private DateTime _cachedAtUtc;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public SqlCampaignContext(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<int?> GetCurrentCampaignRecordIdAsync(CancellationToken ct = default)
    {
        // Cache (best-effort)
        if (_cached.HasValue && (DateTime.UtcNow - _cachedAtUtc) <= CacheTtl)
            return _cached;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var id = await TryGetFromCampaignsTableAsync(conn, ct).ConfigureAwait(false);
        Cache(id);
        return id;
    }

    private void Cache(int? value)
    {
        _cached = value;
        _cachedAtUtc = DateTime.UtcNow;
    }

    private static async Task<int?> TryGetFromCampaignsTableAsync(SqlConnection conn, CancellationToken ct)
    {
        if (!await TableExistsAsync(conn, "CGCampaigns", ct).ConfigureAwait(false))
            return null;

        var cols = await GetColumnsAsync(conn, "CGCampaigns", ct).ConfigureAwait(false);

        // Candidate "active" columns.
        var activeCols = new[] { "IsActive", "IsCurrent", "IsDefault", "IsSelected", "IsOpen" };
        var activeCol = activeCols.FirstOrDefault(c => cols.Contains(c, StringComparer.OrdinalIgnoreCase));

        if (activeCol is not null)
        {
            var sql = $@"
SELECT TOP (1) CampaignRecordId
FROM dbo.CGCampaigns
WHERE [{activeCol}] = 1
ORDER BY CampaignRecordId DESC;";

            await using var cmd = new SqlCommand(sql, conn);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is not null && obj is not DBNull)
            {
                if (int.TryParse(Convert.ToString(obj), out var activeId) && activeId > 0)
                    return activeId;
            }
        }

        // Fallback: newest campaign id.
        {
            const string sql = "SELECT MAX(CampaignRecordId) FROM dbo.CGCampaigns;";
            await using var cmd = new SqlCommand(sql, conn);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is not null && obj is not DBNull)
            {
                if (int.TryParse(Convert.ToString(obj), out var maxId) && maxId > 0)
                    return maxId;
            }
        }

        return null;
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

    private static async Task<HashSet<string>> GetColumnsAsync(SqlConnection conn, string tableName, CancellationToken ct)
    {
        const string sql = @"
SELECT c.name
FROM sys.columns c
JOIN sys.tables t ON t.object_id = c.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = 'dbo' AND t.name = @Table;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Table", SqlDbType.NVarChar, 128) { Value = tableName });

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var s = r.GetString(0);
            if (!string.IsNullOrWhiteSpace(s))
                set.Add(s.Trim());
        }

        return set;
    }
}
