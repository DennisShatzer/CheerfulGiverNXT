using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Loads the configured "fund exclusions" used when determining whether a donor is a first-time giver
/// for the current campaign.
///
/// Behavior:
/// - Reads dbo.CGFirstTimeFundExclusions for the active CampaignRecordId.
/// - Returns active FundName values as matching tokens (case-insensitive).
/// - If the table doesn't exist or no campaign is configured, returns an empty list.
/// </summary>
public interface IFirstTimeGiverRules
{
    Task<string[]> GetExcludedFundTokensAsync(CancellationToken ct = default);
}

public sealed class SqlFirstTimeGiverRules : IFirstTimeGiverRules
{
    private readonly string _connectionString;
    private readonly ICampaignContext _campaignContext;

    private string[]? _cachedTokens;
    private int? _cachedCampaignId;
    private DateTime _cachedAtUtc;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public SqlFirstTimeGiverRules(string connectionString, ICampaignContext campaignContext)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _campaignContext = campaignContext ?? throw new ArgumentNullException(nameof(campaignContext));
    }

    public async Task<string[]> GetExcludedFundTokensAsync(CancellationToken ct = default)
    {
        var campaignId = await _campaignContext.GetCurrentCampaignRecordIdAsync(ct).ConfigureAwait(false);
        if (!campaignId.HasValue)
            return Array.Empty<string>();

        // Best-effort cache
        if (_cachedTokens is not null
            && _cachedCampaignId == campaignId.Value
            && (DateTime.UtcNow - _cachedAtUtc) <= CacheTtl)
        {
            return _cachedTokens;
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await TableExistsAsync(conn, "CGFirstTimeFundExclusions", ct).ConfigureAwait(false))
            return Array.Empty<string>();

        const string sql = @"
SELECT FundName
FROM dbo.CGFirstTimeFundExclusions
WHERE CampaignRecordId = @CampaignRecordId
  AND IsActive = 1
ORDER BY SortOrder, FundName;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignId.Value });

        var list = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rdr.IsDBNull(0)) continue;

            var s = rdr.GetString(0);
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
        }

        var tokens = list
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _cachedTokens = tokens;
        _cachedCampaignId = campaignId.Value;
        _cachedAtUtc = DateTime.UtcNow;

        return tokens;
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
