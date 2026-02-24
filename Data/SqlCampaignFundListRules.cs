using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Loads the semicolon-separated fund token list from dbo.CGCampaigns.FundList
/// for the active campaign.
/// 
/// Interpretation:
/// - If any prior contributed fund matches ANY token, the donor is NOT a first-time giver.
/// - If no matches are found, the donor IS a first-time giver.
/// 
/// This class is intentionally schema-tolerant:
/// - If CGCampaigns or FundList column does not exist, it returns an empty token list.
/// </summary>
public interface IFundListRules
{
    Task<string[]> GetFundTokensAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the raw semicolon-separated FundList string from dbo.CGCampaigns.FundList
    /// for the active campaign (empty string if unavailable).
    /// </summary>
    Task<string> GetFundListRawAsync(CancellationToken ct = default);
}

public sealed class SqlCampaignFundListRules : IFundListRules
{
    private readonly string _connectionString;
    private readonly ICampaignContext _campaignContext;

    private string[]? _cachedTokens;
    private string? _cachedRaw;
    private int? _cachedCampaignId;
    private DateTime _cachedAtUtc;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public SqlCampaignFundListRules(string connectionString, ICampaignContext campaignContext)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _campaignContext = campaignContext ?? throw new ArgumentNullException(nameof(campaignContext));
    }

    public async Task<string[]> GetFundTokensAsync(CancellationToken ct = default)
    {
        var (raw, tokens) = await GetRawAndTokensAsync(ct).ConfigureAwait(false);
        _ = raw; // keep cached for GetFundListRawAsync
        return tokens;
    }

    public async Task<string> GetFundListRawAsync(CancellationToken ct = default)
    {
        var (raw, tokens) = await GetRawAndTokensAsync(ct).ConfigureAwait(false);
        _ = tokens; // keep cached for GetFundTokensAsync
        return raw;
    }

    private async Task<(string Raw, string[] Tokens)> GetRawAndTokensAsync(CancellationToken ct)
    {
        var campaignId = await _campaignContext.GetCurrentCampaignRecordIdAsync(ct).ConfigureAwait(false);
        if (!campaignId.HasValue)
            return ("", Array.Empty<string>());

        // Cache (best-effort)
        if (_cachedTokens is not null
            && _cachedRaw is not null
            && _cachedCampaignId == campaignId.Value
            && (DateTime.UtcNow - _cachedAtUtc) <= CacheTtl)
        {
            return (_cachedRaw, _cachedTokens);
        }

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await TableExistsAsync(conn, "CGCampaigns", ct).ConfigureAwait(false))
            return ("", Array.Empty<string>());

        // FundList is optional in older DBs.
        if (!await HasFundListColumnAsync(conn, ct).ConfigureAwait(false))
            return ("", Array.Empty<string>());

        const string sql = @"
SELECT FundList
FROM dbo.CGCampaigns
WHERE CampaignRecordId = @CampaignRecordId;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignId.Value });

        var rawObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        var raw = rawObj is null || rawObj is DBNull ? "" : (Convert.ToString(rawObj) ?? "");
        raw = raw.Trim();

        var tokens = raw
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => (s ?? "").Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _cachedRaw = raw;
        _cachedTokens = tokens;
        _cachedCampaignId = campaignId.Value;
        _cachedAtUtc = DateTime.UtcNow;

        return (raw, tokens);
    }

    private static async Task<bool> HasFundListColumnAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = "SELECT CASE WHEN COL_LENGTH('dbo.CGCampaigns', 'FundList') IS NOT NULL THEN 1 ELSE 0 END;";
        await using var cmd = new SqlCommand(sql, conn);
        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return obj is not null && obj is not DBNull && Convert.ToInt32(obj) == 1;
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
