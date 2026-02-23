using CheerfulGiverNXT.Workflow;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

public interface IGiftWorkflowStore
{
    /// <summary>
    /// Persist the workflow + structured gift/sponsorship rows in a single SQL transaction.
    /// Run Sql/001_CreateGiftWorkflowTables.sql once to create tables.
    /// </summary>
    Task SaveAsync(GiftWorkflowContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Updates ONLY the workflow row (Status/CompletedAtUtc/ContextJson, etc.).
    /// Does not touch gift/sponsorship child rows.
    /// Useful for appending audit trail entries after a successful SaveAsync.
    /// </summary>
    Task SaveWorkflowOnlyAsync(GiftWorkflowContext ctx, CancellationToken ct = default);

    /// <summary>
    /// Lists successful gifts previously saved by this app for the given constituent,
    /// optionally filtered to a specific campaign id.
    /// </summary>
    Task<GiftHistoryItem[]> ListSuccessfulGiftsAsync(
        int constituentId,
        string? campaignId,
        int take = 50,
        CancellationToken ct = default);

    /// <summary>
    /// Returns dates that are fully booked for sponsorship (a FULL row exists, or both AM and PM exist)
    /// in CGDatesSponsored for the given date range. If <paramref name="campaignRecordId"/> is null,
    /// returns fully booked dates across all campaigns.
    /// </summary>
    Task<DateTime[]> ListFullyBookedSponsorshipDatesAsync(
        int? campaignRecordId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct = default);

    /// <summary>
    /// Returns reserved DayPart values (FULL/AM/PM) for a specific SponsoredDate.
    /// If <paramref name="campaignRecordId"/> is null, returns reservations across all campaigns.
    /// </summary>
    Task<string[]> ListReservedDayPartsAsync(
        int? campaignRecordId,
        DateTime sponsoredDate,
        CancellationToken ct = default);


    /// <summary>
    /// Lists locally committed workflow transactions (for auditing).
    /// </summary>
    Task<LocalTransactionItem[]> ListLocalTransactionsAsync(
        LocalTransactionQuery query,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the stored ContextJson for a workflow.
    /// </summary>
    Task<string?> GetWorkflowContextJsonAsync(Guid workflowId, CancellationToken ct = default);
}

public sealed record GiftHistoryItem(
    DateTime? PledgeDate,
    decimal Amount,
    string? Frequency,
    int? Installments,
    string? ApiGiftId,
    DateTime? SponsoredDate,
    string? Slot,
    string? Comments,
    DateTime CreatedAtUtc
);


public sealed record LocalTransactionQuery(
    DateTime? FromUtc,
    DateTime? ToUtc,
    string? Search,
    string? Status,
    bool? ApiAttempted,
    bool? ApiSucceeded,
    int Take = 500
);

public sealed record LocalTransactionItem(
    Guid WorkflowId,
    DateTime CreatedAtUtc,
    DateTime? CompletedAtUtc,
    string Status,
    string MachineName,
    string WindowsUser,
    int ConstituentId,
    string? ConstituentName,
    bool? IsFirstTimeGiver,
    bool? IsNewRadioConstituent,
    decimal? Amount,
    string? Frequency,
    int? Installments,
    DateTime? PledgeDate,
    DateTime? ApiAttemptedAtUtc,
    bool? ApiSucceeded,
    string? ApiGiftId,
    string? ApiErrorMessage,
    DateTime? SponsoredDate,
    string? Slot
);

public sealed class SqlGiftWorkflowStore : IGiftWorkflowStore
{
    private readonly string _connectionString;

    public SqlGiftWorkflowStore(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task SaveAsync(GiftWorkflowContext ctx, CancellationToken ct = default)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaPresentAsync(conn, ct).ConfigureAwait(false);

        // Sponsorship reservations (Option A) use dbo.CGDatesSponsored.
        // Verify the table exists when a sponsorship is being saved.
        if (ctx.Gift.Sponsorship.IsEnabled
            && ctx.Gift.Sponsorship.SponsoredDate.HasValue
            && !string.IsNullOrWhiteSpace(ctx.Gift.Sponsorship.Slot))
        {
            var hasDatesSponsored = await TableExistsAsync(conn, "CGDatesSponsored", ct).ConfigureAwait(false);
            if (!hasDatesSponsored)
                throw new InvalidOperationException("CGDatesSponsored table is missing. Run the CheerfulGiver schema script to enable sponsorship reservations.");
        }

        await using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            await UpsertWorkflowAsync(conn, tx, ctx, ct).ConfigureAwait(false);

            // Idempotent: remove any prior CGDatesSponsored rows associated with previous gift rows
            // for this workflow (do this BEFORE deleting the workflow gift rows).
            await DeleteDatesSponsoredRowsForWorkflowAsync(conn, tx, ctx.WorkflowId, ct).ConfigureAwait(false);

            // Idempotent: replace child rows for this workflow
            await DeleteChildrenAsync(conn, tx, ctx.WorkflowId, ct).ConfigureAwait(false);

            var giftRowId = await InsertGiftAsync(conn, tx, ctx, ct).ConfigureAwait(false);

            if (ctx.Gift.Sponsorship.IsEnabled
                && ctx.Gift.Sponsorship.SponsoredDate.HasValue
                && !string.IsNullOrWhiteSpace(ctx.Gift.Sponsorship.Slot))
            {
                await InsertSponsorshipAsync(conn, tx, ctx, ct).ConfigureAwait(false);

                // Reserve the sponsored date/time slot in CGDatesSponsored (Option A).
                await InsertDateSponsoredReservationAsync(conn, tx, ctx, giftRowId, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SaveWorkflowOnlyAsync(GiftWorkflowContext ctx, CancellationToken ct = default)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaPresentAsync(conn, ct).ConfigureAwait(false);

        await using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            await UpsertWorkflowAsync(conn, tx, ctx, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<GiftHistoryItem[]> ListSuccessfulGiftsAsync(
        int constituentId,
        string? campaignId,
        int take = 50,
        CancellationToken ct = default)
    {
        if (take <= 0) return Array.Empty<GiftHistoryItem>();

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaPresentAsync(conn, ct).ConfigureAwait(false);

        const string sql = @"
SELECT TOP (@Take)
    g.PledgeDate,
    g.Amount,
    g.Frequency,
    g.Installments,
    g.ApiGiftId,
    s.SponsoredDate,
    s.Slot,
    g.Comments,
    g.CreatedAtUtc
FROM dbo.CGGiftWorkflowGifts g
LEFT JOIN dbo.CGGiftWorkflowSponsorships s ON s.WorkflowId = g.WorkflowId
WHERE g.ConstituentId = @ConstituentId
  AND (@CampaignId IS NULL OR g.CampaignId = @CampaignId)
  AND g.ApiSucceeded = 1
ORDER BY COALESCE(g.PledgeDate, CONVERT(date, g.CreatedAtUtc)) DESC,
         g.CreatedAtUtc DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Take", SqlDbType.Int) { Value = take });
        cmd.Parameters.Add(new SqlParameter("@ConstituentId", SqlDbType.Int) { Value = constituentId });
        cmd.Parameters.Add(new SqlParameter("@CampaignId", SqlDbType.NVarChar, 50)
        { Value = (object?)campaignId ?? DBNull.Value });

        var items = new List<GiftHistoryItem>(Math.Min(take, 50));

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            DateTime? pledgeDate = r["PledgeDate"] is DBNull ? null : (DateTime)r["PledgeDate"];
            decimal amount = r["Amount"] is DBNull ? 0m : (decimal)r["Amount"];
            string? frequency = r["Frequency"] is DBNull ? null : (string)r["Frequency"];
            int? installments = r["Installments"] is DBNull ? null : (int)r["Installments"];
            string? apiGiftId = r["ApiGiftId"] is DBNull ? null : (string)r["ApiGiftId"];
            DateTime? sponsoredDate = r["SponsoredDate"] is DBNull ? null : (DateTime)r["SponsoredDate"];
            string? slot = r["Slot"] is DBNull ? null : (string)r["Slot"];
            string? comments = r["Comments"] is DBNull ? null : (string)r["Comments"];
            DateTime createdAtUtc = r["CreatedAtUtc"] is DBNull ? DateTime.MinValue : (DateTime)r["CreatedAtUtc"];

            items.Add(new GiftHistoryItem(
                pledgeDate,
                amount,
                frequency,
                installments,
                apiGiftId,
                sponsoredDate,
                slot,
                comments,
                createdAtUtc));
        }

        return items.ToArray();
    }


    public async Task<DateTime[]> ListFullyBookedSponsorshipDatesAsync(
        int? campaignRecordId,
        DateTime startDate,
        DateTime endDate,
        CancellationToken ct = default)
    {
        var start = startDate.Date;
        var end = endDate.Date;
        if (end < start)
            (start, end) = (end, start);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = @"
WITH x AS (
    SELECT
        SponsoredDate,
        MAX(CASE WHEN DayPart = N'FULL' THEN 1 ELSE 0 END) AS HasFull,
        MAX(CASE WHEN DayPart = N'AM'   THEN 1 ELSE 0 END) AS HasAM,
        MAX(CASE WHEN DayPart = N'PM'   THEN 1 ELSE 0 END) AS HasPM
    FROM dbo.CGDatesSponsored
    WHERE IsCancelled = 0
      AND SponsoredDate >= @StartDate
      AND SponsoredDate <= @EndDate
      AND (@CampaignRecordId IS NULL OR CampaignRecordId = @CampaignRecordId)
    GROUP BY SponsoredDate
)
SELECT SponsoredDate
FROM x
WHERE HasFull = 1 OR (HasAM = 1 AND HasPM = 1)
ORDER BY SponsoredDate;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@StartDate", SqlDbType.Date) { Value = start });
        cmd.Parameters.Add(new SqlParameter("@EndDate", SqlDbType.Date) { Value = end });
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int)
        { Value = (object?)campaignRecordId ?? DBNull.Value });

        var list = new List<DateTime>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            if (r[0] is DateTime d)
                list.Add(d.Date);
        }

        return list.ToArray();
    }

    public async Task<string[]> ListReservedDayPartsAsync(
        int? campaignRecordId,
        DateTime sponsoredDate,
        CancellationToken ct = default)
    {
        var d = sponsoredDate.Date;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = @"
SELECT DayPart
FROM dbo.CGDatesSponsored
WHERE IsCancelled = 0
  AND SponsoredDate = @SponsoredDate
  AND (@CampaignRecordId IS NULL OR CampaignRecordId = @CampaignRecordId);";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@SponsoredDate", SqlDbType.Date) { Value = d });
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int)
        { Value = (object?)campaignRecordId ?? DBNull.Value });

        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var s = (r[0] as string ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s.ToUpperInvariant());
        }

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }


    
    public async Task<LocalTransactionItem[]> ListLocalTransactionsAsync(
        LocalTransactionQuery query,
        CancellationToken ct = default)
    {
        query ??= new LocalTransactionQuery(null, null, null, null, null, null, 500);

        var take = query.Take <= 0 ? 500 : Math.Min(query.Take, 5000);

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaPresentAsync(conn, ct).ConfigureAwait(false);

        DateTime? fromUtc = query.FromUtc?.ToUniversalTime();
        DateTime? toUtc = query.ToUtc?.ToUniversalTime();

        string? search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        Guid? workflowId = null;
        int? constituentId = null;
        if (search is not null)
        {
            if (Guid.TryParse(search, out var g))
                workflowId = g;

            if (int.TryParse(search, out var i) && i > 0)
                constituentId = i;
        }

        string? like = search is null ? null : $"%{search.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}%";

        const string sql = @"
SELECT TOP (@Take)
    w.WorkflowId,
    w.CreatedAtUtc,
    w.CompletedAtUtc,
    w.Status,
    w.MachineName,
    w.WindowsUser,
    w.ConstituentId,
    w.ConstituentName,
    w.IsFirstTimeGiver,
    w.IsNewRadioConstituent,
    g.Amount,
    g.Frequency,
    g.Installments,
    g.PledgeDate,
    g.ApiAttemptedAtUtc,
    g.ApiSucceeded,
    g.ApiGiftId,
    g.ApiErrorMessage,
    s.SponsoredDate,
    s.Slot
FROM dbo.CGGiftWorkflows w
LEFT JOIN dbo.CGGiftWorkflowGifts g ON g.WorkflowId = w.WorkflowId
LEFT JOIN dbo.CGGiftWorkflowSponsorships s ON s.WorkflowId = w.WorkflowId
WHERE (@FromUtc IS NULL OR w.CreatedAtUtc >= @FromUtc)
  AND (@ToUtc IS NULL OR w.CreatedAtUtc < @ToUtc)
  AND (@WorkflowId IS NULL OR w.WorkflowId = @WorkflowId)
  AND (@ConstituentId IS NULL OR w.ConstituentId = @ConstituentId)
  AND (@Status IS NULL OR w.Status = @Status)
  AND (@ApiAttempted IS NULL OR (@ApiAttempted = 1 AND g.ApiAttemptedAtUtc IS NOT NULL) OR (@ApiAttempted = 0 AND g.ApiAttemptedAtUtc IS NULL))
  AND (@ApiSucceeded IS NULL OR g.ApiSucceeded = @ApiSucceeded)
  AND (@SearchLike IS NULL OR
        w.ConstituentName LIKE @SearchLike OR
        w.SearchText LIKE @SearchLike OR
        w.WindowsUser LIKE @SearchLike OR
        w.MachineName LIKE @SearchLike OR
        g.ApiGiftId LIKE @SearchLike OR
        g.ApiErrorMessage LIKE @SearchLike)
ORDER BY w.CreatedAtUtc DESC;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Take", SqlDbType.Int) { Value = take });
        cmd.Parameters.Add(new SqlParameter("@FromUtc", SqlDbType.DateTime2) { Value = (object?)fromUtc ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ToUtc", SqlDbType.DateTime2) { Value = (object?)toUtc ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = (object?)workflowId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ConstituentId", SqlDbType.Int) { Value = (object?)constituentId ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 30) { Value = (object?)query.Status ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@ApiAttempted", SqlDbType.Bit) { Value = (object?)query.ApiAttempted ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ApiSucceeded", SqlDbType.Bit) { Value = (object?)query.ApiSucceeded ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@SearchLike", SqlDbType.NVarChar, 240) { Value = (object?)like ?? DBNull.Value });

        var list = new List<LocalTransactionItem>(Math.Min(take, 500));

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var wfId = (Guid)r["WorkflowId"];
            var createdAtUtc = (DateTime)r["CreatedAtUtc"];
            DateTime? completedAtUtc = r["CompletedAtUtc"] is DBNull ? null : (DateTime)r["CompletedAtUtc"];

            var status = (r["Status"] as string ?? "").Trim();
            var machineName = (r["MachineName"] as string ?? "").Trim();
            var windowsUser = (r["WindowsUser"] as string ?? "").Trim();

            var cid = r["ConstituentId"] is DBNull ? 0 : (int)r["ConstituentId"];
            var cname = r["ConstituentName"] is DBNull ? null : (string)r["ConstituentName"];

            bool? isFirst = r["IsFirstTimeGiver"] is DBNull ? null : (bool)r["IsFirstTimeGiver"];
            bool? isRadio = r["IsNewRadioConstituent"] is DBNull ? null : (bool)r["IsNewRadioConstituent"];

            decimal? amount = r["Amount"] is DBNull ? null : (decimal)r["Amount"];
            string? frequency = r["Frequency"] is DBNull ? null : (string)r["Frequency"];
            int? installments = r["Installments"] is DBNull ? null : (int)r["Installments"];

            DateTime? pledgeDate = r["PledgeDate"] is DBNull ? null : (DateTime)r["PledgeDate"];
            DateTime? apiAttemptedAtUtc = r["ApiAttemptedAtUtc"] is DBNull ? null : (DateTime)r["ApiAttemptedAtUtc"];
            bool? apiSucceeded = r["ApiSucceeded"] is DBNull ? null : (bool)r["ApiSucceeded"];
            string? apiGiftId = r["ApiGiftId"] is DBNull ? null : (string)r["ApiGiftId"];
            string? apiError = r["ApiErrorMessage"] is DBNull ? null : (string)r["ApiErrorMessage"];

            DateTime? sponsoredDate = r["SponsoredDate"] is DBNull ? null : (DateTime)r["SponsoredDate"];
            string? slot = r["Slot"] is DBNull ? null : (string)r["Slot"];

            list.Add(new LocalTransactionItem(
                wfId,
                createdAtUtc,
                completedAtUtc,
                status,
                machineName,
                windowsUser,
                cid,
                cname,
                isFirst,
                isRadio,
                amount,
                frequency,
                installments,
                pledgeDate,
                apiAttemptedAtUtc,
                apiSucceeded,
                apiGiftId,
                apiError,
                sponsoredDate,
                slot));
        }

        return list.ToArray();
    }

    public async Task<string?> GetWorkflowContextJsonAsync(Guid workflowId, CancellationToken ct = default)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaPresentAsync(conn, ct).ConfigureAwait(false);

        const string sql = @"SELECT ContextJson FROM dbo.CGGiftWorkflows WHERE WorkflowId = @WorkflowId;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = workflowId });

        var obj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return obj is DBNull or null ? null : (string)obj;
    }

private static async Task EnsureSchemaPresentAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = @"SELECT OBJECT_ID('dbo.CGGiftWorkflows','U') AS Wf,
                                    OBJECT_ID('dbo.CGGiftWorkflowGifts','U') AS Gifts,
                                    OBJECT_ID('dbo.CGGiftWorkflowSponsorships','U') AS Spons";

        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
            throw new InvalidOperationException("Unable to verify workflow schema.");

        bool hasWf = !(r["Wf"] is DBNull);
        bool hasGifts = !(r["Gifts"] is DBNull);
        bool hasSpons = !(r["Spons"] is DBNull);

        if (!hasWf || !hasGifts || !hasSpons)
        {
            throw new InvalidOperationException(
                "Workflow tables are missing. Run Sql/001_CreateGiftWorkflowTables.sql against your CheerfulGiver SQL Express database.");
        }
    }

    private static async Task UpsertWorkflowAsync(SqlConnection conn, SqlTransaction tx, GiftWorkflowContext ctx, CancellationToken ct)
    {
        const string sql = @"
MERGE dbo.CGGiftWorkflows AS tgt
USING (SELECT @WorkflowId AS WorkflowId) AS src
ON tgt.WorkflowId = src.WorkflowId
WHEN MATCHED THEN
    UPDATE SET
        CompletedAtUtc = @CompletedAtUtc,
        MachineName = @MachineName,
        WindowsUser = @WindowsUser,
        Status = @Status,
        SearchText = @SearchText,
        ConstituentId = @ConstituentId,
        ConstituentName = @ConstituentName,
        IsFirstTimeGiver = @IsFirstTimeGiver,
        IsNewRadioConstituent = @IsNewRadioConstituent,
        ContextJson = @ContextJson
WHEN NOT MATCHED THEN
    INSERT (WorkflowId, CreatedAtUtc, CompletedAtUtc, MachineName, WindowsUser, Status, SearchText,
            ConstituentId, ConstituentName, IsFirstTimeGiver, IsNewRadioConstituent, ContextJson)
    VALUES (@WorkflowId, @CreatedAtUtc, @CompletedAtUtc, @MachineName, @WindowsUser, @Status, @SearchText,
            @ConstituentId, @ConstituentName, @IsFirstTimeGiver, @IsNewRadioConstituent, @ContextJson);";

        await using var cmd = new SqlCommand(sql, conn, tx);

        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = ctx.WorkflowId });
        cmd.Parameters.Add(new SqlParameter("@CreatedAtUtc", SqlDbType.DateTime2) { Value = ctx.StartedAtUtc });
        cmd.Parameters.Add(new SqlParameter("@CompletedAtUtc", SqlDbType.DateTime2) { Value = (object?)ctx.CompletedAtUtc ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@MachineName", SqlDbType.NVarChar, 128) { Value = ctx.MachineName });
        cmd.Parameters.Add(new SqlParameter("@WindowsUser", SqlDbType.NVarChar, 128) { Value = ctx.WindowsUser });
        cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 30) { Value = ctx.Status.ToString() });
        cmd.Parameters.Add(new SqlParameter("@SearchText", SqlDbType.NVarChar, 200) { Value = (object?)ctx.SearchText ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@ConstituentId", SqlDbType.Int) { Value = ctx.Constituent.ConstituentId });
        cmd.Parameters.Add(new SqlParameter("@ConstituentName", SqlDbType.NVarChar, 200) { Value = (object?)ctx.Constituent.FullName ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@IsFirstTimeGiver", SqlDbType.Bit) { Value = (object?)ctx.IsFirstTimeGiver ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@IsNewRadioConstituent", SqlDbType.Bit) { Value = (object?)ctx.IsNewRadioConstituent ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@ContextJson", SqlDbType.NVarChar, -1) { Value = ctx.ToJson() });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeleteChildrenAsync(SqlConnection conn, SqlTransaction tx, Guid workflowId, CancellationToken ct)
    {
        const string sql = @"
DELETE FROM dbo.CGGiftWorkflowSponsorships WHERE WorkflowId = @WorkflowId;
DELETE FROM dbo.CGGiftWorkflowGifts WHERE WorkflowId = @WorkflowId;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = workflowId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<int> InsertGiftAsync(SqlConnection conn, SqlTransaction tx, GiftWorkflowContext ctx, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CGGiftWorkflowGifts
(WorkflowId, ConstituentId, Amount, Frequency, Installments, PledgeDate, StartDate,
 FundId, CampaignId, AppealId, PackageId, SendReminder, Comments,
 ApiAttemptedAtUtc, ApiSucceeded, ApiGiftId, ApiErrorMessage, CreatedAtUtc)
OUTPUT INSERTED.Id
VALUES
(@WorkflowId, @ConstituentId, @Amount, @Frequency, @Installments, @PledgeDate, @StartDate,
 @FundId, @CampaignId, @AppealId, @PackageId, @SendReminder, @Comments,
 @ApiAttemptedAtUtc, @ApiSucceeded, @ApiGiftId, @ApiErrorMessage, @CreatedAtUtc);";

        await using var cmd = new SqlCommand(sql, conn, tx);

        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = ctx.WorkflowId });
        cmd.Parameters.Add(new SqlParameter("@ConstituentId", SqlDbType.Int) { Value = ctx.Constituent.ConstituentId });

        cmd.Parameters.Add(new SqlParameter("@Amount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = ctx.Gift.Amount });
        cmd.Parameters.Add(new SqlParameter("@Frequency", SqlDbType.NVarChar, 30) { Value = (object?)ctx.Gift.Frequency ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@Installments", SqlDbType.Int) { Value = (object?)ctx.Gift.Installments ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@PledgeDate", SqlDbType.Date) { Value = (object?)ctx.Gift.PledgeDate?.Date ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@StartDate", SqlDbType.Date) { Value = (object?)ctx.Gift.StartDate?.Date ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@FundId", SqlDbType.NVarChar, 50) { Value = (object?)ctx.Gift.FundId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@CampaignId", SqlDbType.NVarChar, 50) { Value = (object?)ctx.Gift.CampaignId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@AppealId", SqlDbType.NVarChar, 50) { Value = (object?)ctx.Gift.AppealId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@PackageId", SqlDbType.NVarChar, 50) { Value = (object?)ctx.Gift.PackageId ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@SendReminder", SqlDbType.Bit) { Value = ctx.Gift.SendReminder });
        cmd.Parameters.Add(new SqlParameter("@Comments", SqlDbType.NVarChar, 2000) { Value = (object?)ctx.Gift.Comments ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@ApiAttemptedAtUtc", SqlDbType.DateTime2) { Value = (object?)ctx.Api.AttemptedAtUtc ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ApiSucceeded", SqlDbType.Bit) { Value = ctx.Api.Success });
        cmd.Parameters.Add(new SqlParameter("@ApiGiftId", SqlDbType.NVarChar, 50) { Value = (object?)ctx.Api.GiftId ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@ApiErrorMessage", SqlDbType.NVarChar, 2000) { Value = (object?)ctx.Api.ErrorMessage ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@CreatedAtUtc", SqlDbType.DateTime2) { Value = DateTime.UtcNow });

        var newIdObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (newIdObj is null or DBNull)
            throw new InvalidOperationException("Unable to obtain new gift row id.");

        return Convert.ToInt32(newIdObj);
    }

    private static async Task InsertSponsorshipAsync(SqlConnection conn, SqlTransaction tx, GiftWorkflowContext ctx, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CGGiftWorkflowSponsorships
(WorkflowId, ConstituentId, SponsoredDate, Slot, ThresholdAmount, CreatedAtUtc)
VALUES
(@WorkflowId, @ConstituentId, @SponsoredDate, @Slot, @ThresholdAmount, @CreatedAtUtc);";

        await using var cmd = new SqlCommand(sql, conn, tx);

        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = ctx.WorkflowId });
        cmd.Parameters.Add(new SqlParameter("@ConstituentId", SqlDbType.Int) { Value = ctx.Constituent.ConstituentId });

        cmd.Parameters.Add(new SqlParameter("@SponsoredDate", SqlDbType.Date) { Value = ctx.Gift.Sponsorship.SponsoredDate!.Value.Date });
        cmd.Parameters.Add(new SqlParameter("@Slot", SqlDbType.NVarChar, 20) { Value = ctx.Gift.Sponsorship.Slot! });
        cmd.Parameters.Add(new SqlParameter("@ThresholdAmount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = (object?)ctx.Gift.Sponsorship.ThresholdAmount ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@CreatedAtUtc", SqlDbType.DateTime2) { Value = DateTime.UtcNow });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task DeleteDatesSponsoredRowsForWorkflowAsync(
        SqlConnection conn,
        SqlTransaction tx,
        Guid workflowId,
        CancellationToken ct)
    {
        // Only delete rows that were associated with gift rows belonging to this workflow.
        // We do this BEFORE deleting CGGiftWorkflowGifts so the join still works.
        const string sql = @"
DELETE ds
FROM dbo.CGDatesSponsored ds
WHERE ds.GiftRecordId IN (
    SELECT g.Id
    FROM dbo.CGGiftWorkflowGifts g
    WHERE g.WorkflowId = @WorkflowId
);";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = workflowId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task InsertDateSponsoredReservationAsync(
        SqlConnection conn,
        SqlTransaction tx,
        GiftWorkflowContext ctx,
        int giftRowId,
        CancellationToken ct)
    {
        // In this app, Gift.CampaignId holds the LOCAL CampaignRecordId (integer as string).
        if (!int.TryParse((ctx.Gift.CampaignId ?? "").Trim(), out var campaignRecordId) || campaignRecordId <= 0)
            throw new InvalidOperationException("Unable to determine CampaignRecordId for sponsorship reservation.");

        var sponsoredDate = ctx.Gift.Sponsorship.SponsoredDate!.Value.Date;
        var slot = (ctx.Gift.Sponsorship.Slot ?? "").Trim();
        var dayPart = TryParseDayPartFromSlot(slot);
        if (dayPart is null)
            throw new InvalidOperationException("Unable to determine sponsorship DayPart from the selected slot.");

        // Lock existing reservations for this campaign/date to prevent race conditions.
        var existingParts = await ListExistingDayPartsWithLockAsync(conn, tx, campaignRecordId, sponsoredDate, ct).ConfigureAwait(false);

        // Enforce business rules:
        // - FULL blocks everything
        // - AM/PM block themselves
        if (Array.Exists(existingParts, p => string.Equals(p, "FULL", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That sponsorship date is already booked (FULL).");

        if (string.Equals(dayPart, "FULL", StringComparison.OrdinalIgnoreCase))
        {
            if (existingParts.Length > 0)
                throw new InvalidOperationException("That sponsorship date is already booked.");
        }
        else
        {
            if (Array.Exists(existingParts, p => string.Equals(p, dayPart, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("That sponsorship date/time is already booked.");
        }

        var required = ctx.Gift.Sponsorship.ThresholdAmount ?? ctx.Gift.Amount;
        if (required < 0m) required = 0m;

        const string insertSql = @"
INSERT INTO dbo.CGDatesSponsored
(CampaignRecordId, ConstituentRecordId, GiftRecordId, SponsoredDate, DayPart, SponsorTier, RequiredAmount, IsCancelled)
VALUES
(@CampaignRecordId, @ConstituentRecordId, @GiftRecordId, @SponsoredDate, @DayPart, @SponsorTier, @RequiredAmount, 0);";

        await using var cmd = new SqlCommand(insertSql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });
        cmd.Parameters.Add(new SqlParameter("@ConstituentRecordId", SqlDbType.Int) { Value = ctx.Constituent.ConstituentId });
        cmd.Parameters.Add(new SqlParameter("@GiftRecordId", SqlDbType.Int) { Value = giftRowId });
        cmd.Parameters.Add(new SqlParameter("@SponsoredDate", SqlDbType.Date) { Value = sponsoredDate });
        cmd.Parameters.Add(new SqlParameter("@DayPart", SqlDbType.NVarChar, 4) { Value = dayPart });
        cmd.Parameters.Add(new SqlParameter("@SponsorTier", SqlDbType.NVarChar, 20) { Value = string.IsNullOrWhiteSpace(slot) ? (object)DBNull.Value : slot });
        cmd.Parameters.Add(new SqlParameter("@RequiredAmount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = required });

        try
        {
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // Unique constraint violation (if present) – treat as already booked.
            throw new InvalidOperationException("That sponsorship date/time is already booked.", ex);
        }
    }

    private static async Task<string[]> ListExistingDayPartsWithLockAsync(
        SqlConnection conn,
        SqlTransaction tx,
        int campaignRecordId,
        DateTime sponsoredDate,
        CancellationToken ct)
    {
        const string sql = @"
SELECT DayPart
FROM dbo.CGDatesSponsored WITH (UPDLOCK, HOLDLOCK)
WHERE IsCancelled = 0
  AND CampaignRecordId = @CampaignRecordId
  AND SponsoredDate = @SponsoredDate;";

        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add(new SqlParameter("@CampaignRecordId", SqlDbType.Int) { Value = campaignRecordId });
        cmd.Parameters.Add(new SqlParameter("@SponsoredDate", SqlDbType.Date) { Value = sponsoredDate.Date });

        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var s = (r[0] as string ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s.ToUpperInvariant());
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? TryParseDayPartFromSlot(string slot)
    {
        var s = (slot ?? "").Trim();
        if (s.Length == 0) return null;

        if (s.IndexOf("full", StringComparison.OrdinalIgnoreCase) >= 0) return "FULL";
        if (s.IndexOf("am", StringComparison.OrdinalIgnoreCase) >= 0) return "AM";
        if (s.IndexOf("pm", StringComparison.OrdinalIgnoreCase) >= 0) return "PM";

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
}
