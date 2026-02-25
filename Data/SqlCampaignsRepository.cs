using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
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

    /// <summary>
    /// Semicolon-separated fund tokens used to determine first-time giver status.
    /// If any prior contributed fund matches any token, the donor is NOT a first-time giver.
    /// </summary>
    public string FundList { get; set; } = "";

    /// <summary>
    /// Constituent ID used as a placeholder when a caller wants to give anonymously.
    /// Stored per-campaign in dbo.CGCampaigns.AnonymousGiverId (nullable).
    /// </summary>
    public int? AnonymousGiverId { get; set; }

    public decimal SponsorshipHalfDayAmount { get; set; } = 1000m;
    public decimal SponsorshipFullDayAmount { get; set; } = 2000m;
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

        var hasSponsorshipCols = await HasSponsorshipThresholdColumnsAsync(conn, tx: null, ct).ConfigureAwait(false);
        var hasFundListCol = await HasFundListColumnAsync(conn, tx: null, ct).ConfigureAwait(false);
        var hasAnonymousGiverIdCol = await HasAnonymousGiverIdColumnAsync(conn, tx: null, ct).ConfigureAwait(false);

        // Build a single SELECT that matches the schema we detected.
        var cols = new List<string>
        {
            "CampaignRecordId",
            "CampaignName",
            "StartLocal",
            "EndLocalExclusive",
            "TimeZoneId",
            "GoalAmount",
            "GoalFirstTimeGivers"
        };

        if (hasFundListCol) cols.Add("FundList");
        if (hasSponsorshipCols)
        {
            cols.Add("SponsorshipHalfDayAmount");
            cols.Add("SponsorshipFullDayAmount");
        }
        if (hasAnonymousGiverIdCol) cols.Add("AnonymousGiverId");

        cols.AddRange(new[]
        {
            "IsActive",
            "CreatedAt",
            "CreatedBy",
            "UpdatedAt",
            "UpdatedBy"
        });

        // IMPORTANT:
        // Use real newlines in the SQL.
        // In a verbatim string literal ( @"..." ), sequences like "\n" are NOT escape sequences;
        // they become a literal backslash + n, which SQL Server will treat as invalid syntax.
        var sql = $@"
SELECT
    {string.Join(",\n    ", cols)}
FROM dbo.CGCampaigns
ORDER BY StartLocal DESC, CampaignRecordId DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        var list = new List<CampaignRow>();

        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await rdr.ReadAsync(ct).ConfigureAwait(false))
        {
            var i = 0;
            var idxCampaignRecordId = i++;
            var idxCampaignName = i++;
            var idxStartLocal = i++;
            var idxEndLocalExclusive = i++;
            var idxTimeZoneId = i++;
            var idxGoalAmount = i++;
            var idxGoalFirstTimeGivers = i++;

            int? idxFundList = null;
            if (hasFundListCol)
                idxFundList = i++;

            int? idxHalfDay = null;
            int? idxFullDay = null;
            if (hasSponsorshipCols)
            {
                idxHalfDay = i++;
                idxFullDay = i++;
            }

            int? idxAnonymous = null;
            if (hasAnonymousGiverIdCol)
                idxAnonymous = i++;

            var idxIsActive = i++;
            var idxCreatedAt = i++;
            var idxCreatedBy = i++;
            var idxUpdatedAt = i++;
            var idxUpdatedBy = i++;

            var half = 1000m;
            var full = 2000m;

            if (hasSponsorshipCols && idxHalfDay.HasValue && idxFullDay.HasValue)
            {
                if (!rdr.IsDBNull(idxHalfDay.Value)) half = rdr.GetDecimal(idxHalfDay.Value);
                if (!rdr.IsDBNull(idxFullDay.Value)) full = rdr.GetDecimal(idxFullDay.Value);
                NormalizeThresholds(ref half, ref full);
            }

            var fundList = "";
            if (hasFundListCol && idxFundList.HasValue && !rdr.IsDBNull(idxFundList.Value))
                fundList = (rdr.GetString(idxFundList.Value) ?? "").Trim();

            int? anonId = null;
            if (hasAnonymousGiverIdCol && idxAnonymous.HasValue && !rdr.IsDBNull(idxAnonymous.Value))
            {
                var v = rdr.GetInt32(idxAnonymous.Value);
                if (v > 0) anonId = v;
            }

            list.Add(new CampaignRow
            {
                CampaignRecordId = rdr.GetInt32(idxCampaignRecordId),
                CampaignName = (rdr.IsDBNull(idxCampaignName) ? "" : (rdr.GetString(idxCampaignName) ?? "")).Trim(),
                StartLocal = rdr.GetDateTime(idxStartLocal),
                EndLocalExclusive = rdr.GetDateTime(idxEndLocalExclusive),
                TimeZoneId = (rdr.IsDBNull(idxTimeZoneId) ? "" : (rdr.GetString(idxTimeZoneId) ?? "")).Trim(),
                GoalAmount = rdr.IsDBNull(idxGoalAmount) ? null : rdr.GetDecimal(idxGoalAmount),
                GoalFirstTimeGivers = rdr.IsDBNull(idxGoalFirstTimeGivers) ? null : rdr.GetInt32(idxGoalFirstTimeGivers),
                FundList = fundList,
                AnonymousGiverId = anonId,
                SponsorshipHalfDayAmount = half,
                SponsorshipFullDayAmount = full,
                IsActive = !rdr.IsDBNull(idxIsActive) && rdr.GetBoolean(idxIsActive),
                CreatedAt = rdr.GetDateTime(idxCreatedAt),
                CreatedBy = rdr.IsDBNull(idxCreatedBy) ? null : rdr.GetString(idxCreatedBy),
                UpdatedAt = rdr.GetDateTime(idxUpdatedAt),
                UpdatedBy = rdr.IsDBNull(idxUpdatedBy) ? null : rdr.GetString(idxUpdatedBy)
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
            // NOTE:
            // We are inside an explicit SQL transaction (tx). Any commands executed on this connection
            // MUST have cmd.Transaction set, otherwise ADO.NET will throw:
            // "BeginExecuteReader requires the command to have a transaction when the connection assigned
            //  to the command is in a pending local transaction."
            //
            // Therefore, even schema-detection helpers that query sys tables or COL_LENGTH must participate
            // in the same transaction.
            var hasSponsorshipCols = await HasSponsorshipThresholdColumnsAsync(conn, tx, ct).ConfigureAwait(false);
            var hasFundListCol = await HasFundListColumnAsync(conn, tx, ct).ConfigureAwait(false);
            var hasAnonymousGiverIdCol = await HasAnonymousGiverIdColumnAsync(conn, tx, ct).ConfigureAwait(false);

            var insertCols = new List<string>
            {
                "CampaignName",
                "StartLocal",
                "EndLocalExclusive",
                "TimeZoneId",
                "GoalAmount",
                "GoalFirstTimeGivers"
            };
            var insertVals = new List<string>
            {
                "@CampaignName",
                "@StartLocal",
                "@EndLocalExclusive",
                "@TimeZoneId",
                "@GoalAmount",
                "@GoalFirstTimeGivers"
            };

            if (hasFundListCol)
            {
                insertCols.Add("FundList");
                insertVals.Add("@FundList");
            }
            if (hasSponsorshipCols)
            {
                insertCols.Add("SponsorshipHalfDayAmount");
                insertVals.Add("@SponsorshipHalfDayAmount");
                insertCols.Add("SponsorshipFullDayAmount");
                insertVals.Add("@SponsorshipFullDayAmount");
            }
            if (hasAnonymousGiverIdCol)
            {
                insertCols.Add("AnonymousGiverId");
                insertVals.Add("@AnonymousGiverId");
            }

            insertCols.AddRange(new[] { "IsActive", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy" });
            insertVals.AddRange(new[] { "@IsActive", "SYSUTCDATETIME()", "@User", "SYSUTCDATETIME()", "@User" });

            var insSql = $@"
INSERT INTO dbo.CGCampaigns
    ({string.Join(", ", insertCols)})
VALUES
    ({string.Join(", ", insertVals)});
SELECT CAST(SCOPE_IDENTITY() AS int);";

            var setParts = new List<string>
            {
                "CampaignName = @CampaignName",
                "StartLocal = @StartLocal",
                "EndLocalExclusive = @EndLocalExclusive",
                "TimeZoneId = @TimeZoneId",
                "GoalAmount = @GoalAmount",
                "GoalFirstTimeGivers = @GoalFirstTimeGivers"
            };

            if (hasFundListCol) setParts.Add("FundList = @FundList");
            if (hasSponsorshipCols)
            {
                setParts.Add("SponsorshipHalfDayAmount = @SponsorshipHalfDayAmount");
                setParts.Add("SponsorshipFullDayAmount = @SponsorshipFullDayAmount");
            }
            if (hasAnonymousGiverIdCol) setParts.Add("AnonymousGiverId = @AnonymousGiverId");

            setParts.Add("IsActive = @IsActive");
            setParts.Add("UpdatedAt = SYSUTCDATETIME()");
            setParts.Add("UpdatedBy = @User");

            var updSql = $@"
UPDATE dbo.CGCampaigns
SET
    {string.Join(",\n    ", setParts)}
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
                    AddParams(cmd, r, name, tz, username, includeId: false, includeSponsorship: hasSponsorshipCols, includeFundList: hasFundListCol, includeAnonymous: hasAnonymousGiverIdCol);
                    var newIdObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    if (newIdObj is not null && newIdObj is not DBNull)
                        r.CampaignRecordId = Convert.ToInt32(newIdObj);
                }
                else
                {
                    await using var cmd = new SqlCommand(updSql, conn, tx);
                    AddParams(cmd, r, name, tz, username, includeId: true, includeSponsorship: hasSponsorshipCols, includeFundList: hasFundListCol, includeAnonymous: hasAnonymousGiverIdCol);
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

    private static void AddParams(SqlCommand cmd, CampaignRow r, string name, string tz, string user, bool includeId, bool includeSponsorship, bool includeFundList, bool includeAnonymous)
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

        if (includeFundList)
        {
            var fundList = (r.FundList ?? "").Trim();
            cmd.Parameters.Add(new SqlParameter("@FundList", SqlDbType.NVarChar) { Value = fundList });
        }

        if (includeSponsorship)
        {
            var half = r.SponsorshipHalfDayAmount;
            var full = r.SponsorshipFullDayAmount;
            NormalizeThresholds(ref half, ref full);

            cmd.Parameters.Add(new SqlParameter("@SponsorshipHalfDayAmount", SqlDbType.Decimal)
            {
                Precision = 18,
                Scale = 2,
                Value = half
            });
            cmd.Parameters.Add(new SqlParameter("@SponsorshipFullDayAmount", SqlDbType.Decimal)
            {
                Precision = 18,
                Scale = 2,
                Value = full
            });
        }

        if (includeAnonymous)
        {
            int? anon = null;
            if (r.AnonymousGiverId.HasValue && r.AnonymousGiverId.Value > 0)
                anon = r.AnonymousGiverId.Value;

            cmd.Parameters.Add(new SqlParameter("@AnonymousGiverId", SqlDbType.Int)
            {
                Value = (object?)anon ?? DBNull.Value
            });
        }

        cmd.Parameters.Add(new SqlParameter("@IsActive", SqlDbType.Bit) { Value = r.IsActive });
        cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 200) { Value = (object?)user ?? DBNull.Value });
    }

    private static void NormalizeThresholds(ref decimal half, ref decimal full)
    {
        const decimal DefaultHalf = 1000m;
        const decimal DefaultFull = 2000m;

        if (half <= 0m) half = DefaultHalf;
        if (full <= 0m) full = DefaultFull;

        if (full < half)
            full = Math.Max(half, DefaultFull);
    }

    private static async Task<bool> HasSponsorshipThresholdColumnsAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        // Safe to call even if the columns don't exist.
        const string sql = @"
SELECT
    CASE WHEN COL_LENGTH('dbo.CGCampaigns', 'SponsorshipHalfDayAmount') IS NOT NULL THEN 1 ELSE 0 END AS HasHalf,
    CASE WHEN COL_LENGTH('dbo.CGCampaigns', 'SponsorshipFullDayAmount') IS NOT NULL THEN 1 ELSE 0 END AS HasFull;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        await using var rdr = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await rdr.ReadAsync(ct).ConfigureAwait(false))
            return false;

        var hasHalf = !rdr.IsDBNull(0) && rdr.GetInt32(0) == 1;
        var hasFull = !rdr.IsDBNull(1) && rdr.GetInt32(1) == 1;
        return hasHalf && hasFull;
    }

    private static async Task<bool> HasFundListColumnAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        const string sql = @"SELECT CASE WHEN COL_LENGTH('dbo.CGCampaigns', 'FundList') IS NOT NULL THEN 1 ELSE 0 END;";
        await using var cmd = new SqlCommand(sql, conn, tx);
        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return obj is not null && obj is not DBNull && Convert.ToInt32(obj) == 1;
    }

    private static async Task<bool> HasAnonymousGiverIdColumnAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        const string sql = @"SELECT CASE WHEN COL_LENGTH('dbo.CGCampaigns', 'AnonymousGiverId') IS NOT NULL THEN 1 ELSE 0 END;";
        await using var cmd = new SqlCommand(sql, conn, tx);
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
