using CheerfulGiverNXT.Workflow;
using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
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

        await using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);

        try
        {
            await UpsertWorkflowAsync(conn, tx, ctx, ct).ConfigureAwait(false);

            // Idempotent: replace child rows for this workflow
            await DeleteChildrenAsync(conn, tx, ctx.WorkflowId, ct).ConfigureAwait(false);

            await InsertGiftAsync(conn, tx, ctx, ct).ConfigureAwait(false);

            if (ctx.Gift.Sponsorship.IsEnabled
                && ctx.Gift.Sponsorship.SponsoredDate.HasValue
                && !string.IsNullOrWhiteSpace(ctx.Gift.Sponsorship.Slot))
            {
                await InsertSponsorshipAsync(conn, tx, ctx, ct).ConfigureAwait(false);
            }

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

    private static async Task InsertGiftAsync(SqlConnection conn, SqlTransaction tx, GiftWorkflowContext ctx, CancellationToken ct)
    {
        const string sql = @"
INSERT INTO dbo.CGGiftWorkflowGifts
(WorkflowId, ConstituentId, Amount, Frequency, Installments, PledgeDate, StartDate,
 FundId, CampaignId, AppealId, PackageId, SendReminder, Comments,
 ApiAttemptedAtUtc, ApiSucceeded, ApiGiftId, ApiErrorMessage, CreatedAtUtc)
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

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
}
