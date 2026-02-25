using Microsoft.Data.SqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverSQP.SkyQueue;

public sealed class SqlSkyTransactionRepository
{
    private readonly string _connectionString;

    public SqlSkyTransactionRepository(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<(int Pending, int Processing, int Succeeded, int Failed)> GetStatusCountsAsync(CancellationToken ct = default)
    {
        const string sql = @"
SELECT TransactionStatus, COUNT(*) AS Cnt
FROM dbo.CGSKYTransactions
GROUP BY TransactionStatus;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        int pending = 0, processing = 0, succeeded = 0, failed = 0;

        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var status = (r[0] as string ?? "").Trim();
            var cnt = r.GetInt32(1);

            switch (status)
            {
                case "Pending": pending = cnt; break;
                case "Processing": processing = cnt; break;
                case "Succeeded": succeeded = cnt; break;
                case "Failed": failed = cnt; break;
            }
        }

        return (pending, processing, succeeded, failed);
    }

    public async Task<IReadOnlyList<SkyTransactionRow>> GetRecentAsync(string statusFilter, int top = 200, CancellationToken ct = default)
    {
        top = Math.Clamp(top, 1, 1000);
        var hasFilter = !string.IsNullOrWhiteSpace(statusFilter);

        var sql = hasFilter
            ? @"
SELECT TOP (@Top)
    SkyTransactionRecordId, WorkflowId, TransactionType, TransactionStatus, StatusNote,
    EnqueuedAtUtc, EnqueuedAtLocal,
    ClientMachineName, ClientWindowsUser,
    ConstituentId, Amount, PledgeDate, FundId, Comments,
    RequestJson,
    ProcessingAttemptCount,
    ProcessingStartedAtUtc, ProcessingStartedAtLocal,
    ProcessingCompletedAtUtc, ProcessingCompletedAtLocal,
    LastProcessingAttemptAtUtc, LastProcessingAttemptAtLocal,
    LastProcessingErrorMessage,
    ProcessedGiftId
FROM dbo.CGSKYTransactions
WHERE TransactionStatus = @Status
ORDER BY SkyTransactionRecordId DESC;"
            : @"
SELECT TOP (@Top)
    SkyTransactionRecordId, WorkflowId, TransactionType, TransactionStatus, StatusNote,
    EnqueuedAtUtc, EnqueuedAtLocal,
    ClientMachineName, ClientWindowsUser,
    ConstituentId, Amount, PledgeDate, FundId, Comments,
    RequestJson,
    ProcessingAttemptCount,
    ProcessingStartedAtUtc, ProcessingStartedAtLocal,
    ProcessingCompletedAtUtc, ProcessingCompletedAtLocal,
    LastProcessingAttemptAtUtc, LastProcessingAttemptAtLocal,
    LastProcessingErrorMessage,
    ProcessedGiftId
FROM dbo.CGSKYTransactions
ORDER BY SkyTransactionRecordId DESC;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Top", SqlDbType.Int) { Value = top });

        if (hasFilter)
            cmd.Parameters.Add(new SqlParameter("@Status", SqlDbType.NVarChar, 30) { Value = statusFilter.Trim() });

        var list = new List<SkyTransactionRow>(top);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(MapRow(r));

        return list;
    }

    /// <summary>
    /// Reset stale "Processing" rows back to "Pending" so a crashed worker doesn't block the queue.
    /// </summary>
    public async Task<int> ResetStaleProcessingAsync(int staleProcessingMinutes, string workerLabel, CancellationToken ct = default)
    {
        staleProcessingMinutes = Math.Max(1, staleProcessingMinutes);
        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;

        var tz = TimeZoneInfo.Local;
        var offsetMinutes = (int)Math.Round(tz.GetUtcOffset(nowLocal).TotalMinutes);

        const string sql = @"
UPDATE dbo.CGSKYTransactions
SET
    TransactionStatus = 'Pending',
    StatusNote = CONCAT('Reset from stale Processing by ', @WorkerLabel, ' at ', CONVERT(varchar(19), @NowUtc, 120), 'Z'),
    UpdatedAtUtc = @NowUtc,
    UpdatedAtLocal = @NowLocal,
    UpdatedLocalTimeZoneId = @TzId,
    UpdatedLocalUtcOffsetMinutes = @OffsetMinutes
WHERE
    TransactionStatus = 'Processing'
    AND ProcessingCompletedAtUtc IS NULL
    AND ProcessingStartedAtUtc IS NOT NULL
    AND ProcessingStartedAtUtc < DATEADD(MINUTE, -@StaleMinutes, SYSUTCDATETIME());";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@WorkerLabel", SqlDbType.NVarChar, 128) { Value = workerLabel });
        cmd.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
        cmd.Parameters.Add(new SqlParameter("@NowLocal", SqlDbType.DateTime2) { Value = nowLocal });
        cmd.Parameters.Add(new SqlParameter("@TzId", SqlDbType.NVarChar, 100) { Value = tz.Id });
        cmd.Parameters.Add(new SqlParameter("@OffsetMinutes", SqlDbType.Int) { Value = offsetMinutes });
        cmd.Parameters.Add(new SqlParameter("@StaleMinutes", SqlDbType.Int) { Value = staleProcessingMinutes });

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically claim a batch of pending rows by moving them to "Processing".
    /// </summary>
    public async Task<IReadOnlyList<SkyTransactionRow>> ClaimPendingBatchAsync(
        int batchSize,
        string workerLabel,
        int staleProcessingMinutes,
        int maxAttempts,
        CancellationToken ct = default)
    {
        batchSize = Math.Clamp(batchSize, 1, 200);
        maxAttempts = Math.Max(1, maxAttempts);

        // Safety: reset stale Processing rows first.
        await ResetStaleProcessingAsync(staleProcessingMinutes, workerLabel, ct).ConfigureAwait(false);

        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;

        var tz = TimeZoneInfo.Local;
        var offsetMinutes = (int)Math.Round(tz.GetUtcOffset(nowLocal).TotalMinutes);

        // Claim oldest pending rows first.
        const string sql = @"
;WITH cte AS
(
    SELECT TOP (@BatchSize) SkyTransactionRecordId
    FROM dbo.CGSKYTransactions WITH (READPAST, UPDLOCK, ROWLOCK)
    WHERE
        TransactionStatus = 'Pending'
        AND ProcessingAttemptCount < @MaxAttempts
    ORDER BY EnqueuedAtUtc ASC
)
UPDATE t
SET
    TransactionStatus = 'Processing',
    ProcessingStartedAtUtc = COALESCE(t.ProcessingStartedAtUtc, @NowUtc),
    ProcessingStartedAtLocal = COALESCE(t.ProcessingStartedAtLocal, @NowLocal),
    LastProcessingAttemptAtUtc = @NowUtc,
    LastProcessingAttemptAtLocal = @NowLocal,
    ProcessingAttemptCount = t.ProcessingAttemptCount + 1,
    StatusNote = CONCAT('Claimed by ', @WorkerLabel, ' at ', CONVERT(varchar(19), @NowUtc, 120), 'Z'),
    UpdatedAtUtc = @NowUtc,
    UpdatedAtLocal = @NowLocal,
    UpdatedLocalTimeZoneId = @TzId,
    UpdatedLocalUtcOffsetMinutes = @OffsetMinutes
OUTPUT
    inserted.SkyTransactionRecordId,
    inserted.WorkflowId,
    inserted.TransactionType,
    inserted.TransactionStatus,
    inserted.StatusNote,
    inserted.EnqueuedAtUtc,
    inserted.EnqueuedAtLocal,
    inserted.ClientMachineName,
    inserted.ClientWindowsUser,
    inserted.ConstituentId,
    inserted.Amount,
    inserted.PledgeDate,
    inserted.FundId,
    inserted.Comments,
    inserted.RequestJson,
    inserted.ProcessingAttemptCount,
    inserted.ProcessingStartedAtUtc,
    inserted.ProcessingStartedAtLocal,
    inserted.ProcessingCompletedAtUtc,
    inserted.ProcessingCompletedAtLocal,
    inserted.LastProcessingAttemptAtUtc,
    inserted.LastProcessingAttemptAtLocal,
    inserted.LastProcessingErrorMessage,
    inserted.ProcessedGiftId
FROM dbo.CGSKYTransactions t
INNER JOIN cte ON cte.SkyTransactionRecordId = t.SkyTransactionRecordId;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@BatchSize", SqlDbType.Int) { Value = batchSize });
        cmd.Parameters.Add(new SqlParameter("@MaxAttempts", SqlDbType.Int) { Value = maxAttempts });
        cmd.Parameters.Add(new SqlParameter("@WorkerLabel", SqlDbType.NVarChar, 128) { Value = workerLabel });
        cmd.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
        cmd.Parameters.Add(new SqlParameter("@NowLocal", SqlDbType.DateTime2) { Value = nowLocal });
        cmd.Parameters.Add(new SqlParameter("@TzId", SqlDbType.NVarChar, 100) { Value = tz.Id });
        cmd.Parameters.Add(new SqlParameter("@OffsetMinutes", SqlDbType.Int) { Value = offsetMinutes });

        var list = new List<SkyTransactionRow>(batchSize);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
            list.Add(MapRow(r));

        return list;
    }

    public async Task MarkSucceededAsync(int skyTransactionRecordId, string processedGiftId, string? statusNote, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;

        var tz = TimeZoneInfo.Local;
        var offsetMinutes = (int)Math.Round(tz.GetUtcOffset(nowLocal).TotalMinutes);

        const string sql = @"
UPDATE dbo.CGSKYTransactions
SET
    TransactionStatus = 'Succeeded',
    StatusNote = @StatusNote,
    ProcessedGiftId = @GiftId,
    LastProcessingErrorMessage = NULL,
    ProcessingCompletedAtUtc = @NowUtc,
    ProcessingCompletedAtLocal = @NowLocal,
    UpdatedAtUtc = @NowUtc,
    UpdatedAtLocal = @NowLocal,
    UpdatedLocalTimeZoneId = @TzId,
    UpdatedLocalUtcOffsetMinutes = @OffsetMinutes
WHERE SkyTransactionRecordId = @Id;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = skyTransactionRecordId });
        cmd.Parameters.Add(new SqlParameter("@GiftId", SqlDbType.NVarChar, 50) { Value = processedGiftId });
        cmd.Parameters.Add(new SqlParameter("@StatusNote", SqlDbType.NVarChar, 2000) { Value = (object?)Trunc(statusNote, 2000) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
        cmd.Parameters.Add(new SqlParameter("@NowLocal", SqlDbType.DateTime2) { Value = nowLocal });
        cmd.Parameters.Add(new SqlParameter("@TzId", SqlDbType.NVarChar, 100) { Value = tz.Id });
        cmd.Parameters.Add(new SqlParameter("@OffsetMinutes", SqlDbType.Int) { Value = offsetMinutes });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkFailedAsync(int skyTransactionRecordId, string errorMessage, string? statusNote, CancellationToken ct = default)
    {
        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;

        var tz = TimeZoneInfo.Local;
        var offsetMinutes = (int)Math.Round(tz.GetUtcOffset(nowLocal).TotalMinutes);

        const string sql = @"
UPDATE dbo.CGSKYTransactions
SET
    TransactionStatus = 'Failed',
    StatusNote = @StatusNote,
    LastProcessingErrorMessage = @Err,
    ProcessingCompletedAtUtc = @NowUtc,
    ProcessingCompletedAtLocal = @NowLocal,
    UpdatedAtUtc = @NowUtc,
    UpdatedAtLocal = @NowLocal,
    UpdatedLocalTimeZoneId = @TzId,
    UpdatedLocalUtcOffsetMinutes = @OffsetMinutes
WHERE SkyTransactionRecordId = @Id;";

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = skyTransactionRecordId });
        cmd.Parameters.Add(new SqlParameter("@Err", SqlDbType.NVarChar, 2000) { Value = Trunc(errorMessage, 2000) });
        cmd.Parameters.Add(new SqlParameter("@StatusNote", SqlDbType.NVarChar, 2000) { Value = (object?)Trunc(statusNote, 2000) ?? DBNull.Value });
        cmd.Parameters.Add(new SqlParameter("@NowUtc", SqlDbType.DateTime2) { Value = nowUtc });
        cmd.Parameters.Add(new SqlParameter("@NowLocal", SqlDbType.DateTime2) { Value = nowLocal });
        cmd.Parameters.Add(new SqlParameter("@TzId", SqlDbType.NVarChar, 100) { Value = tz.Id });
        cmd.Parameters.Add(new SqlParameter("@OffsetMinutes", SqlDbType.Int) { Value = offsetMinutes });

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SkyTransactionRow MapRow(SqlDataReader r)
    {
        // Keep mapping strict and explicit to avoid silent drift.
        var i = 0;
        var id = r.GetInt32(i++);
        var workflowId = r.GetGuid(i++);
        var type = r.GetString(i++);
        var status = r.GetString(i++);
        var note = r.IsDBNull(i) ? null : r.GetString(i); i++;

        var enqUtc = r.GetDateTime(i++);
        var enqLocal = r.GetDateTime(i++);

        var clientMachine = r.GetString(i++);
        var clientUser = r.GetString(i++);

        var constituentId = r.GetInt32(i++);
        var amount = r.GetDecimal(i++);
        var pledgeDate = r.GetDateTime(i++);
        var fundId = r.GetString(i++);
        var comments = r.IsDBNull(i) ? null : r.GetString(i); i++;

        var reqJson = r.GetString(i++);

        var attempts = r.GetInt32(i++);

        DateTime? startedUtc = r.IsDBNull(i) ? null : r.GetDateTime(i); i++;
        DateTime? startedLocal = r.IsDBNull(i) ? null : r.GetDateTime(i); i++;
        DateTime? completedUtc = r.IsDBNull(i) ? null : r.GetDateTime(i); i++;
        DateTime? completedLocal = r.IsDBNull(i) ? null : r.GetDateTime(i); i++;
        DateTime? lastAttemptUtc = r.IsDBNull(i) ? null : r.GetDateTime(i); i++;
        DateTime? lastAttemptLocal = r.IsDBNull(i) ? null : r.GetDateTime(i); i++;
        var lastErr = r.IsDBNull(i) ? null : r.GetString(i); i++;
        var giftId = r.IsDBNull(i) ? null : r.GetString(i);

        return new SkyTransactionRow(
            SkyTransactionRecordId: id,
            WorkflowId: workflowId,
            TransactionType: type,
            TransactionStatus: status,
            StatusNote: note,
            EnqueuedAtUtc: enqUtc,
            EnqueuedAtLocal: enqLocal,
            ClientMachineName: clientMachine,
            ClientWindowsUser: clientUser,
            ConstituentId: constituentId,
            Amount: amount,
            PledgeDate: pledgeDate,
            FundId: fundId,
            Comments: comments,
            RequestJson: reqJson,
            ProcessingAttemptCount: attempts,
            ProcessingStartedAtUtc: startedUtc,
            ProcessingStartedAtLocal: startedLocal,
            ProcessingCompletedAtUtc: completedUtc,
            ProcessingCompletedAtLocal: completedLocal,
            LastProcessingAttemptAtUtc: lastAttemptUtc,
            LastProcessingAttemptAtLocal: lastAttemptLocal,
            LastProcessingErrorMessage: lastErr,
            ProcessedGiftId: giftId);
    }

    private static string Trunc(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Trim();
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
