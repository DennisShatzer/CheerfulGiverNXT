using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Reads the current CampaignRecordId from the same SQL database used by the app.
/// 
/// This implementation is intentionally resilient to schema drift:
/// - If a key/value settings table exists (CGAppSettings / CGSettings / CGAppConfig), it will prefer that.
/// - Otherwise it will attempt to use an "active" flag on CGCampaigns (IsActive / IsCurrent / IsDefault).
/// - Otherwise it falls back to the most recently created CampaignRecordId (MAX).
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

        // 1) Prefer key/value settings tables, if present.
        var fromSettings = await TryGetFromSettingsTableAsync(conn, ct).ConfigureAwait(false);
        if (fromSettings.HasValue)
        {
            Cache(fromSettings);
            return fromSettings;
        }

        // 2) Prefer "active" flags on CGCampaigns, if present.
        var fromCampaigns = await TryGetFromCampaignsTableAsync(conn, ct).ConfigureAwait(false);
        if (fromCampaigns.HasValue)
        {
            Cache(fromCampaigns);
            return fromCampaigns;
        }

        // 3) Fallback: infer from related tables (exclusions / sponsorships), if available.
        var inferred = await TryInferFromRelatedTablesAsync(conn, ct).ConfigureAwait(false);
        Cache(inferred);
        return inferred;
    }

    private void Cache(int? value)
    {
        _cached = value;
        _cachedAtUtc = DateTime.UtcNow;
    }

    private static async Task<int?> TryGetFromSettingsTableAsync(SqlConnection conn, CancellationToken ct)
    {
        // Candidate tables + candidate key/value column names.
        // We detect table existence first and then try a small set of conventional schemas.
        var tableCandidates = new[] { "CGAppSettings", "CGSettings", "CGAppConfig", "CGConfiguration", "CGConfig" };
        var keyCandidates = new[] { "SettingKey", "[Key]", "ConfigKey", "Name" };
        var valueCandidates = new[] { "SettingValue", "[Value]", "ConfigValue", "Value" };

        foreach (var table in tableCandidates)
        {
            if (!await TableExistsAsync(conn, table, ct).ConfigureAwait(false))
                continue;

            var cols = await GetColumnsAsync(conn, table, ct).ConfigureAwait(false);
            var keyCol = keyCandidates.FirstOrDefault(k => cols.Contains(Unbracket(k), StringComparer.OrdinalIgnoreCase));
            var valCol = valueCandidates.FirstOrDefault(v => cols.Contains(Unbracket(v), StringComparer.OrdinalIgnoreCase));

            if (keyCol is null || valCol is null)
                continue;

            // Look for an ActiveCampaignRecordId entry.
            var keyName = Unbracket(keyCol);
            var valName = Unbracket(valCol);
            var sql = $@"
SELECT TOP (1) [{valName}]
FROM dbo.[{table}]
WHERE [{keyName}] = @Key;";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@Key", SqlDbType.NVarChar, 200) { Value = "ActiveCampaignRecordId" });

            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is null || obj is DBNull) continue;

            var s = Convert.ToString(obj)?.Trim();
            if (int.TryParse(s, out var id) && id > 0)
                return id;
        }

        return null;
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
                if (int.TryParse(Convert.ToString(obj), out var id) && id > 0)
                    return id;
            }
        }

        // Fallback: newest campaign id.
        {
            const string sql = "SELECT MAX(CampaignRecordId) FROM dbo.CGCampaigns;";
            await using var cmd = new SqlCommand(sql, conn);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is not null && obj is not DBNull)
            {
                if (int.TryParse(Convert.ToString(obj), out var id) && id > 0)
                    return id;
            }
        }

        return null;
    }

    private static async Task<int?> TryInferFromRelatedTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        // If exclusions exist, infer the most recently created/used campaign id.
        // Preferred table name after the radio-funds refactor:
        //   dbo.CGFirstTimeGiverFundExclusions
        // Legacy table name (older installs):
        //   dbo.CGFirstTimeFundExclusions

        if (await TableExistsAsync(conn, "CGFirstTimeGiverFundExclusions", ct).ConfigureAwait(false))
        {
            const string sql = @"
SELECT TOP (1) CampaignRecordId
FROM dbo.CGFirstTimeGiverFundExclusions
WHERE IsActive = 1
ORDER BY CreatedAt DESC, CampaignRecordId DESC;";

            await using var cmd = new SqlCommand(sql, conn);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is not null && obj is not DBNull)
            {
                if (int.TryParse(Convert.ToString(obj), out var id) && id > 0)
                    return id;
            }
        }
        else if (await TableExistsAsync(conn, "CGFirstTimeFundExclusions", ct).ConfigureAwait(false))
        {
            // Only attempt if the legacy schema is present (CampaignRecordId column).
            var cols = await GetColumnsAsync(conn, "CGFirstTimeFundExclusions", ct).ConfigureAwait(false);
            if (cols.Contains("CampaignRecordId", StringComparer.OrdinalIgnoreCase))
            {
                const string sql = @"
SELECT TOP (1) CampaignRecordId
FROM dbo.CGFirstTimeFundExclusions
WHERE IsActive = 1
ORDER BY CreatedAt DESC, CampaignRecordId DESC;";

                await using var cmd = new SqlCommand(sql, conn);
                var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (obj is not null && obj is not DBNull)
                {
                    if (int.TryParse(Convert.ToString(obj), out var id) && id > 0)
                        return id;
                }
            }
        }

        // If sponsorships exist, infer the most recently used campaign id.
        if (await TableExistsAsync(conn, "CGDatesSponsored", ct).ConfigureAwait(false))
        {
            const string sql = @"
SELECT TOP (1) CampaignRecordId
FROM dbo.CGDatesSponsored
WHERE IsCancelled = 0
ORDER BY SponsoredDate DESC, SponsoredDateRecordId DESC;";

            await using var cmd = new SqlCommand(sql, conn);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (obj is not null && obj is not DBNull)
            {
                if (int.TryParse(Convert.ToString(obj), out var id) && id > 0)
                    return id;
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

    private static string Unbracket(string col)
    {
        // e.g. "[Key]" -> "Key"
        if (col.StartsWith("[", StringComparison.Ordinal) && col.EndsWith("]", StringComparison.Ordinal) && col.Length > 2)
            return col.Substring(1, col.Length - 2);
        return col;
    }
}
