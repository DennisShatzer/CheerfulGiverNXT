using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Loads the configured fund tokens used to determine whether an EXISTING constituent
/// is a "new radio giver".
///
/// Desired DB shape (single record, semicolon-separated):
///   dbo.CGFirstTimeFundExclusions(FundTokens NVARCHAR(MAX) NOT NULL)
///
/// Backward compatibility:
/// - If the table/column isn't present yet, returns empty, allowing callers to fall back to App.config.
/// </summary>
public interface IRadioFundRules
{
    Task<string[]> GetRadioFundTokensAsync(CancellationToken ct = default);
}

public sealed class SqlRadioFundRules : IRadioFundRules
{
    private readonly string _connectionString;

    private string[]? _cached;
    private DateTime _cachedAtUtc;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(2);

    public SqlRadioFundRules(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<string[]> GetRadioFundTokensAsync(CancellationToken ct = default)
    {
        if (_cached is not null && (DateTime.UtcNow - _cachedAtUtc) <= CacheTtl)
            return _cached;

        try
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            if (!await TableExistsAsync(conn, "CGFirstTimeFundExclusions", ct).ConfigureAwait(false))
                return Cache(Array.Empty<string>());

            // Prefer the new, simple schema: FundTokens (or similar).
            var tokenCol = await FindFirstExistingColumnAsync(conn, "CGFirstTimeFundExclusions",
                new[] { "FundTokens", "Funds", "FundList", "Tokens" }, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(tokenCol))
                return Cache(Array.Empty<string>());

            var sql = $"SELECT TOP (1) [{tokenCol}] FROM dbo.CGFirstTimeFundExclusions;";
            await using var cmd = new SqlCommand(sql, conn);
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var raw = (obj is null || obj is DBNull) ? "" : (Convert.ToString(obj) ?? "");

            var tokens = ParseTokens(raw);
            return Cache(tokens);
        }
        catch
        {
            // Fail closed (no tokens) so callers can decide whether to fall back.
            return Cache(Array.Empty<string>());
        }
    }

    private string[] Cache(string[] tokens)
    {
        _cached = tokens;
        _cachedAtUtc = DateTime.UtcNow;
        return tokens;
    }

    private static string[] ParseTokens(string raw)
    {
        raw = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        return raw
            .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => (t ?? "").Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static async Task<string?> FindFirstExistingColumnAsync(
        SqlConnection conn,
        string tableName,
        IReadOnlyList<string> candidates,
        CancellationToken ct)
    {
        var cols = await GetColumnsAsync(conn, tableName, ct).ConfigureAwait(false);
        foreach (var c in candidates)
        {
            if (cols.Contains(c, StringComparer.OrdinalIgnoreCase))
                return cols.First(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase));
        }

        return null;
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
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rdr.IsDBNull(0)) continue;
            var name = rdr.GetString(0);
            if (!string.IsNullOrWhiteSpace(name))
                set.Add(name);
        }

        return set;
    }
}
