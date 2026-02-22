using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Workflow;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Services;

/// <summary>
/// Implements "gift match challenges" backed by SQL Server:
/// - Challenges live in dbo.CGChallenges (ChallengeType = 4, Goal = Budget)
/// - Allocations are recorded in dbo.CGGiftWorkflowChallengeMatches
/// - Anonymous "match donor" constituent id lives in dbo.CGAppSettings (SettingKey = AnonymousMatchConstituentId)
///
/// IMPORTANT:
/// Matching gifts are NOT sent to SKY API. They are recorded locally in SQL Express only.
/// </summary>
public sealed class SqlGiftMatchService : IGiftMatchService
{
    private const string AnonymousMatchKey = "AnonymousMatchConstituentId";
    private const byte ChallengeTypeGiftMatch = 4;

    private readonly string _connectionString;
    private readonly ICampaignContext _campaignContext;
    private readonly IGiftWorkflowStore _workflowStore;

    public SqlGiftMatchService(
        string connectionString,
        ICampaignContext campaignContext,
        IGiftWorkflowStore workflowStore)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _campaignContext = campaignContext ?? throw new ArgumentNullException(nameof(campaignContext));
        _workflowStore = workflowStore ?? throw new ArgumentNullException(nameof(workflowStore));
    }

    public async Task<MatchAdminSnapshot> GetAdminSnapshotAsync(CancellationToken ct = default)
    {
        var campaignRecordId = await _campaignContext.GetCurrentCampaignRecordIdAsync(ct).ConfigureAwait(false);

        int? anon = null;
        MatchChallengeAdminRow[] challenges = Array.Empty<MatchChallengeAdminRow>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaOrThrowAsync(conn, ct).ConfigureAwait(false);

        anon = await TryGetAnonymousMatchConstituentIdAsync(conn, ct).ConfigureAwait(false);

        if (campaignRecordId.HasValue)
            challenges = await ListChallengeRowsAsync(conn, campaignRecordId.Value, includeInactive: true, ct).ConfigureAwait(false);

        return new MatchAdminSnapshot(
            CampaignRecordId: campaignRecordId,
            AnonymousMatchConstituentId: anon,
            Challenges: challenges);
    }

    public async Task SetAnonymousMatchConstituentIdAsync(int constituentId, CancellationToken ct = default)
    {
        if (constituentId <= 0) throw new ArgumentOutOfRangeException(nameof(constituentId));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaOrThrowAsync(conn, ct).ConfigureAwait(false);

        const string sql = @"
MERGE dbo.CGAppSettings AS tgt
USING (SELECT @Key AS SettingKey) AS src
ON tgt.SettingKey = src.SettingKey
WHEN MATCHED THEN
    UPDATE SET
        SettingValue = @Value,
        SettingType = N'int',
        [Description] = N'Constituent ID representing the anonymous match donor (local-only matching)',
        UpdatedAt = SYSUTCDATETIME(),
        UpdatedBy = @User
WHEN NOT MATCHED THEN
    INSERT (SettingKey, SettingValue, SettingType, [Description], UpdatedAt, UpdatedBy)
    VALUES (@Key, @Value, N'int', N'Constituent ID representing the anonymous match donor (local-only matching)', SYSUTCDATETIME(), @User);
";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Key", SqlDbType.NVarChar, 100) { Value = AnonymousMatchKey });
        cmd.Parameters.Add(new SqlParameter("@Value", SqlDbType.NVarChar, 200) { Value = constituentId.ToString(CultureInfo.InvariantCulture) });
        cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 100) { Value = Environment.UserName });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CreateMatchChallengeAsync(string name, decimal budget, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Challenge name is required.", nameof(name));
        if (budget <= 0m) throw new ArgumentOutOfRangeException(nameof(budget));

        var campaignRecordId = await _campaignContext.GetCurrentCampaignRecordIdAsync(ct).ConfigureAwait(false);
        if (!campaignRecordId.HasValue)
            throw new InvalidOperationException("No current CampaignRecordId could be determined. Set ActiveCampaignRecordId in CGAppSettings or mark a campaign active.");

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaOrThrowAsync(conn, ct).ConfigureAwait(false);

        // SortOrder is optional but helps consistent ordering.
        const string nextSortSql = @"
SELECT ISNULL(MAX(SortOrder),0) + 1
FROM dbo.CGChallenges
WHERE CampaignRecordId = @CampaignRecordId
  AND ChallengeType = @ChallengeType;";

        int nextSort;
        await using (var cmd = new SqlCommand(nextSortSql, conn))
        {
            cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId.Value });
            cmd.Parameters.Add(new SqlParameter("@ChallengeType", SqlDbType.TinyInt) { Value = ChallengeTypeGiftMatch });
            var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            nextSort = obj is null || obj is DBNull ? 0 : Convert.ToInt32(obj, CultureInfo.InvariantCulture);
        }

        const string insertSql = @"
INSERT INTO dbo.CGChallenges
(CampaignRecordId, Name, ChallengeType, StartLocal, EndLocalExclusive, Goal, SortOrder, IsActive,
 EligibleMaxGiftAmount, PerGiftCap, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy)
VALUES
(@CampaignRecordId, @Name, @ChallengeType, NULL, NULL, @Goal, @SortOrder, 1,
 NULL, NULL, SYSUTCDATETIME(), @User, SYSUTCDATETIME(), @User);";

        await using (var cmd = new SqlCommand(insertSql, conn))
        {
            cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId.Value });
            cmd.Parameters.Add(new SqlParameter("@Name", SqlDbType.NVarChar, 200) { Value = name });
            cmd.Parameters.Add(new SqlParameter("@ChallengeType", SqlDbType.TinyInt) { Value = ChallengeTypeGiftMatch });
            cmd.Parameters.Add(new SqlParameter("@Goal", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = budget });
            cmd.Parameters.Add(new SqlParameter("@SortOrder", SqlDbType.Int) { Value = nextSort });
            cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 200) { Value = Environment.UserName });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task DeactivateChallengeAsync(int challengeRecordId, CancellationToken ct = default)
    {
        if (challengeRecordId <= 0) throw new ArgumentOutOfRangeException(nameof(challengeRecordId));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaOrThrowAsync(conn, ct).ConfigureAwait(false);

        const string sql = @"
UPDATE dbo.CGChallenges
SET IsActive = 0,
    UpdatedAt = SYSUTCDATETIME(),
    UpdatedBy = @User
WHERE ChallengeRecordId = @Id;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = challengeRecordId });
        cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 200) { Value = Environment.UserName });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<MatchApplyResult> ApplyMatchesForGiftAsync(GiftWorkflowContext sourceWorkflow, CancellationToken ct = default)
    {
        if (sourceWorkflow is null) throw new ArgumentNullException(nameof(sourceWorkflow));

        // Never throw for common "not configured" cases.
        if (!sourceWorkflow.Api.Success || string.IsNullOrWhiteSpace(sourceWorkflow.Api.GiftId))
            return new MatchApplyResult(0m, Array.Empty<MatchAllocation>(), Array.Empty<string>());

        if (sourceWorkflow.Gift.Amount <= 0m)
            return new MatchApplyResult(0m, Array.Empty<MatchAllocation>(), Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(sourceWorkflow.Gift.FundId))
            return new MatchApplyResult(0m, Array.Empty<MatchAllocation>(), new[] { "No FundId on gift; match skipped." });

        var campaignRecordId = await _campaignContext.GetCurrentCampaignRecordIdAsync(ct).ConfigureAwait(false);
        if (!campaignRecordId.HasValue)
            return new MatchApplyResult(0m, Array.Empty<MatchAllocation>(), new[] { "No current CampaignRecordId; match skipped." });

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (!await HasRequiredSchemaAsync(conn, ct).ConfigureAwait(false))
        {
            return new MatchApplyResult(
                0m,
                Array.Empty<MatchAllocation>(),
                new[] { "Match tables not found in SQL. Run Sql/010_GiftMatchChallenges.sql to enable matching." });
        }

        var anonId = await TryGetAnonymousMatchConstituentIdAsync(conn, ct).ConfigureAwait(false);
        if (!anonId.HasValue)
        {
            return new MatchApplyResult(
                0m,
                Array.Empty<MatchAllocation>(),
                new[] { "Anonymous match donor constituent id is not set. Press Ctrl+Shift+C and set AnonymousMatchConstituentId." });
        }

        // Serialize allocations across operators.
        await SqlAppLock.AcquireAsync(conn, "CG_GiftMatchChallenges", ct).ConfigureAwait(false);
        try
        {
            // Re-load within the lock so our remaining-budget math is accurate.
            var active = await ListChallengeRowsAsync(conn, campaignRecordId.Value, includeInactive: false, ct).ConfigureAwait(false);
            var usable = active
                .Where(c => c.IsActive && c.Remaining > 0m)
                .OrderBy(c => c.CreatedAtUtc)
                .ThenBy(c => c.ChallengeRecordId)
                .ToArray();

            if (usable.Length == 0)
                return new MatchApplyResult(0m, Array.Empty<MatchAllocation>(), Array.Empty<string>());

            var warnings = new List<string>();
            var allocations = new List<MatchAllocation>();

            var matchRemaining = sourceWorkflow.Gift.Amount;

            foreach (var ch in usable)
            {
                if (matchRemaining <= 0m) break;

                var take = Math.Min(ch.Remaining, matchRemaining);
                if (take <= 0m) continue;

                try
                {
                    // Local-only: record a "match workflow" and budget allocation in SQL.
                    // No SKY API calls are made for matching gifts.
                    var matchWorkflow = BuildLocalMatchWorkflow(sourceWorkflow, anonId.Value, take, ch.Name);

                    // Persist match workflow locally.
                    await _workflowStore.SaveAsync(matchWorkflow, ct).ConfigureAwait(false);

                    // Record the allocation (counts toward budget when ApiSucceeded=1; here it means "applied successfully").
                    await InsertMatchRowAsync(
                        conn,
                        sourceWorkflow,
                        ch.ChallengeRecordId,
                        ch.Name,
                        take,
                        matchWorkflow.WorkflowId,
                        matchedApiGiftId: null,
                        apiSucceeded: true,
                        apiError: null,
                        ct).ConfigureAwait(false);

                    allocations.Add(new MatchAllocation(ch.ChallengeRecordId, ch.Name, take, MatchedApiGiftId: null));
                    matchRemaining -= take;
                }
                catch (Exception ex)
                {
                    warnings.Add($"Challenge '{ch.Name}' match failed: {ex.Message}");

                    // Best-effort: record failure row for audit (does NOT consume budget because ApiSucceeded=0).
                    try
                    {
                        await InsertMatchRowAsync(
                            conn,
                            sourceWorkflow,
                            ch.ChallengeRecordId,
                            ch.Name,
                            0m,
                            matchedWorkflowId: null,
                            matchedApiGiftId: null,
                            apiSucceeded: false,
                            apiError: ex.Message,
                            ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // ignore
                    }

                    // Stop allocating further: preserve "oldest first" semantics.
                    break;
                }
            }

            // Auto-close challenges that have reached budget.
            await DeactivateDepletedChallengesAsync(conn, campaignRecordId.Value, ct).ConfigureAwait(false);

            var total = allocations.Sum(a => a.AmountMatched);
            return new MatchApplyResult(total, allocations.ToArray(), warnings.ToArray());
        }
        finally
        {
            try { await SqlAppLock.ReleaseAsync(conn, "CG_GiftMatchChallenges", ct).ConfigureAwait(false); } catch { /* ignore */ }
        }
    }

    private static GiftWorkflowContext BuildLocalMatchWorkflow(
        GiftWorkflowContext source,
        int anonConstituentId,
        decimal amount,
        string challengeName)
    {
        var wf = GiftWorkflowContext.Start(
            searchText: $"MATCH:{challengeName}",
            snapshot: new ConstituentSnapshot
            {
                ConstituentId = anonConstituentId,
                FullName = "Anonymous Match Donor"
            });

        wf.Gift.Amount = amount;
        wf.Gift.Frequency = source.Gift.Frequency;
        wf.Gift.Installments = source.Gift.Installments;
        wf.Gift.PledgeDate = source.Gift.PledgeDate;
        wf.Gift.StartDate = source.Gift.StartDate;

        wf.Gift.FundId = source.Gift.FundId;
        wf.Gift.CampaignId = source.Gift.CampaignId;
        wf.Gift.AppealId = source.Gift.AppealId;
        wf.Gift.PackageId = source.Gift.PackageId;

        wf.Gift.SendReminder = false;
        wf.Gift.Comments = BuildMatchComments(source, challengeName);

        // No sponsorship selector on match gifts.
        wf.Gift.Sponsorship.IsEnabled = false;

        // Local-only: no SKY API attempt.
        wf.Api.Attempted = false;
        wf.Api.AttemptedAtUtc = null;
        wf.Api.Success = false;
        wf.Api.GiftId = null;

        // Mark as completed/audited locally.
        wf.Status = WorkflowStatus.Committed;
        wf.CompletedAtUtc = DateTime.UtcNow;

        return wf;
    }

    private static string BuildMatchComments(GiftWorkflowContext source, string challengeName)
    {
        var parts = new List<string>();
        parts.Add($"MATCH: {challengeName}");
        if (!string.IsNullOrWhiteSpace(source.Api.GiftId))
            parts.Add($"OriginalGiftId: {source.Api.GiftId}");
        parts.Add($"OriginalConstituentId: {source.Constituent.ConstituentId}");

        var baseComments = (source.Gift.Comments ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(baseComments))
            parts.Add($"Note: {baseComments}");

        return string.Join(" | ", parts);
    }

    private static async Task InsertMatchRowAsync(
        SqlConnection conn,
        GiftWorkflowContext source,
        int challengeRecordId,
        string challengeName,
        decimal amount,
        Guid? matchedWorkflowId,
        string? matchedApiGiftId,
        bool apiSucceeded,
        string? apiError,
        CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CGGiftWorkflowChallengeMatches
(SourceWorkflowId, SourceApiGiftId, ChallengeRecordId, MatchAmount,
 MatchedWorkflowId, MatchedApiGiftId, ApiSucceeded, ApiErrorMessage, CreatedAtUtc)
VALUES
(@SourceWorkflowId, @SourceApiGiftId, @ChallengeRecordId, @MatchAmount,
 @MatchedWorkflowId, @MatchedApiGiftId, @ApiSucceeded, @ApiErrorMessage, SYSUTCDATETIME());";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@SourceWorkflowId", SqlDbType.UniqueIdentifier) { Value = source.WorkflowId });
        cmd.Parameters.Add(new SqlParameter("@SourceApiGiftId", SqlDbType.NVarChar, 50) { Value = (object?)source.Api.GiftId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ChallengeRecordId", SqlDbType.Int) { Value = challengeRecordId });
        cmd.Parameters.Add(new SqlParameter("@MatchAmount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = amount });
        cmd.Parameters.Add(new SqlParameter("@MatchedWorkflowId", SqlDbType.UniqueIdentifier) { Value = (object?)matchedWorkflowId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@MatchedApiGiftId", SqlDbType.NVarChar, 50) { Value = (object?)matchedApiGiftId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ApiSucceeded", SqlDbType.Bit) { Value = apiSucceeded });
        cmd.Parameters.Add(new SqlParameter("@ApiErrorMessage", SqlDbType.NVarChar, 2000) { Value = (object?)apiError ?? DBNull.Value });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeactivateDepletedChallengesAsync(SqlConnection conn, int campaignRecordId, CancellationToken ct)
    {
        const string sql = @"
UPDATE c
SET c.IsActive = 0,
    c.UpdatedAt = SYSUTCDATETIME(),
    c.UpdatedBy = @User
FROM dbo.CGChallenges c
WHERE c.CampaignRecordId = @CampaignRecordId
  AND c.ChallengeType = @ChallengeType
  AND c.IsActive = 1
  AND c.Goal <= (
      SELECT ISNULL(SUM(m.MatchAmount),0)
      FROM dbo.CGGiftWorkflowChallengeMatches m
      WHERE m.ChallengeRecordId = c.ChallengeRecordId
        AND m.ApiSucceeded = 1
  );";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });
        cmd.Parameters.Add(new SqlParameter("@ChallengeType", SqlDbType.TinyInt) { Value = ChallengeTypeGiftMatch });
        cmd.Parameters.Add(new SqlParameter("@User", SqlDbType.NVarChar, 200) { Value = Environment.UserName });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<MatchChallengeAdminRow[]> ListChallengeRowsAsync(
        SqlConnection conn,
        int campaignRecordId,
        bool includeInactive,
        CancellationToken ct)
    {
        var sql = @"
SELECT
    c.ChallengeRecordId,
    c.Name,
    c.Goal AS Budget,
    ISNULL(SUM(CASE WHEN m.ApiSucceeded = 1 THEN m.MatchAmount ELSE 0 END), 0) AS Used,
    (c.Goal - ISNULL(SUM(CASE WHEN m.ApiSucceeded = 1 THEN m.MatchAmount ELSE 0 END), 0)) AS Remaining,
    c.CreatedAt,
    c.IsActive
FROM dbo.CGChallenges c
LEFT JOIN dbo.CGGiftWorkflowChallengeMatches m
    ON m.ChallengeRecordId = c.ChallengeRecordId
WHERE c.CampaignRecordId = @CampaignRecordId
  AND c.ChallengeType = @ChallengeType
" + (includeInactive ? "" : "  AND c.IsActive = 1\n") + @"
GROUP BY c.ChallengeRecordId, c.Name, c.Goal, c.CreatedAt, c.IsActive
ORDER BY c.CreatedAt ASC, c.ChallengeRecordId ASC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });
        cmd.Parameters.Add(new SqlParameter("@ChallengeType", SqlDbType.TinyInt) { Value = ChallengeTypeGiftMatch });

        var list = new List<MatchChallengeAdminRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = Convert.ToInt32(r[0], CultureInfo.InvariantCulture);
            var name = Convert.ToString(r[1]) ?? "";
            var budget = r[2] is DBNull ? 0m : (decimal)r[2];
            var used = r[3] is DBNull ? 0m : (decimal)r[3];
            var remaining = r[4] is DBNull ? 0m : (decimal)r[4];
            var createdAt = r[5] is DBNull ? DateTime.MinValue : (DateTime)r[5];
            var isActive = r[6] is DBNull ? false : (bool)r[6];

            list.Add(new MatchChallengeAdminRow(
                ChallengeRecordId: id,
                Name: name,
                Budget: budget,
                Used: used,
                Remaining: remaining,
                CreatedAtUtc: DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
                IsActive: isActive));
        }

        return list.ToArray();
    }

    private static async Task<int?> TryGetAnonymousMatchConstituentIdAsync(SqlConnection conn, CancellationToken ct)
    {
        // CGAppSettings.SettingValue is nvarchar.
        const string sql = @"
SELECT TOP (1) SettingValue
FROM dbo.CGAppSettings
WHERE SettingKey = @Key;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Key", SqlDbType.NVarChar, 100) { Value = AnonymousMatchKey });
        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (obj is null || obj is DBNull) return null;

        var s = Convert.ToString(obj)?.Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0)
            return id;

        return null;
    }

    private static async Task<bool> HasRequiredSchemaAsync(SqlConnection conn, CancellationToken ct)
    {
        // We only need these tables; everything else is optional.
        return await TableExistsAsync(conn, "CGChallenges", ct).ConfigureAwait(false)
            && await TableExistsAsync(conn, "CGAppSettings", ct).ConfigureAwait(false)
            && await TableExistsAsync(conn, "CGGiftWorkflowChallengeMatches", ct).ConfigureAwait(false);
    }

    private static async Task EnsureSchemaOrThrowAsync(SqlConnection conn, CancellationToken ct)
    {
        if (!await HasRequiredSchemaAsync(conn, ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Gift match schema is missing. Run Sql/010_GiftMatchChallenges.sql against your CheerfulGiver database.");
        }
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
