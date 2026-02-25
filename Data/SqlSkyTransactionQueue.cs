using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Server-side SKY transaction queue.
///
/// The client NEVER posts gifts directly to SKY API. Instead, it enqueues a JSON request into
/// dbo.CGSKYTransactions for a server-side worker to process.
///
/// NOTE:
/// - Timestamps are stored as BOTH UTC and local time (plus timezone + offset) for admin QoL.
/// - This queue is intentionally implemented as a small, self-contained table so the separation between
///   client UI workflow storage (dbo.CGGiftWorkflows*) and server posting is clean.
/// - Enqueue is idempotent per (WorkflowId, TransactionType).
/// </summary>
public interface ISkyTransactionQueue
{
    /// <summary>
    /// Enqueues (or updates) a pending pledge-create transaction for server-side posting.
    /// Returns the CGSKYTransactions primary key.
    /// </summary>
    Task<int> EnqueueOrUpdatePendingPledgeCreateAsync(
        Guid workflowId,
        int constituentId,
        decimal amount,
        DateTime pledgeDate,
        string fundId,
        string? comments,
        string requestJson,
        string clientMachineName,
        string clientWindowsUser,
        CancellationToken ct = default);
}

public sealed class SqlSkyTransactionQueue : ISkyTransactionQueue
{
    private const string TransactionTypePledgeCreate = "PledgeCreate";
    private const string StatusPending = "Pending";

    private readonly string _connectionString;

    public SqlSkyTransactionQueue(string connectionString)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    }

    public async Task<int> EnqueueOrUpdatePendingPledgeCreateAsync(
        Guid workflowId,
        int constituentId,
        decimal amount,
        DateTime pledgeDate,
        string fundId,
        string? comments,
        string requestJson,
        string clientMachineName,
        string clientWindowsUser,
        CancellationToken ct = default)
    {
        if (workflowId == Guid.Empty) throw new ArgumentException("WorkflowId is required.", nameof(workflowId));
        if (constituentId <= 0) throw new ArgumentOutOfRangeException(nameof(constituentId));
        if (amount <= 0m) throw new ArgumentOutOfRangeException(nameof(amount));
        if (string.IsNullOrWhiteSpace(fundId)) throw new ArgumentNullException(nameof(fundId));
        if (string.IsNullOrWhiteSpace(requestJson)) throw new ArgumentNullException(nameof(requestJson));
        if (string.IsNullOrWhiteSpace(clientMachineName)) clientMachineName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(clientWindowsUser)) clientWindowsUser = Environment.UserName;

        var enqueuedAtUtc = DateTime.UtcNow;
        var enqueuedAtLocal = DateTime.Now;

        // Store TZ metadata so the local timestamps remain interpretable.
        var tzId = SafeGetLocalTimeZoneId();
        var offsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(enqueuedAtUtc).TotalMinutes;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await EnsureSchemaPresentAsync(conn, ct).ConfigureAwait(false);

        await using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        try
        {
            // Idempotent per (WorkflowId, TransactionType).
            const string existsSql = @"
SELECT TOP (1) SkyTransactionRecordId
FROM dbo.CGSKYTransactions
WHERE WorkflowId = @WorkflowId
  AND TransactionType = @TransactionType;
";

            int? existingId = null;
            await using (var cmd = new SqlCommand(existsSql, conn, tx))
            {
                cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = workflowId });
                cmd.Parameters.Add(new SqlParameter("@TransactionType", SqlDbType.NVarChar, 50) { Value = TransactionTypePledgeCreate });
                var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (v is not null && v is not DBNull)
                    existingId = Convert.ToInt32(v);
            }

            if (existingId.HasValue)
            {
                const string updateSql = @"
UPDATE dbo.CGSKYTransactions
SET TransactionStatus = @StatusPending,
    StatusNote = NULL,
    RequestJson = @RequestJson,
    ConstituentId = @ConstituentId,
    Amount = @Amount,
    PledgeDate = @PledgeDate,
    FundId = @FundId,
    Comments = @Comments,
    EnqueuedAtUtc = @EnqueuedAtUtc,
    EnqueuedAtLocal = @EnqueuedAtLocal,
    EnqueuedLocalTimeZoneId = @EnqueuedLocalTimeZoneId,
    EnqueuedLocalUtcOffsetMinutes = @EnqueuedLocalUtcOffsetMinutes,
    ClientMachineName = @ClientMachineName,
    ClientWindowsUser = @ClientWindowsUser,
    UpdatedAtUtc = @EnqueuedAtUtc,
    UpdatedAtLocal = @EnqueuedAtLocal,
    UpdatedLocalTimeZoneId = @EnqueuedLocalTimeZoneId,
    UpdatedLocalUtcOffsetMinutes = @EnqueuedLocalUtcOffsetMinutes,
    ProcessingAttemptCount = 0,
    ProcessingStartedAtUtc = NULL,
    ProcessingStartedAtLocal = NULL,
    ProcessingCompletedAtUtc = NULL,
    ProcessingCompletedAtLocal = NULL,
    LastProcessingAttemptAtUtc = NULL,
    LastProcessingAttemptAtLocal = NULL,
    LastProcessingErrorMessage = NULL,
    ProcessedGiftId = NULL
WHERE SkyTransactionRecordId = @Id;
";

                await using var cmd = new SqlCommand(updateSql, conn, tx);
                AddCommonParams(
                    cmd,
                    workflowId,
                    constituentId,
                    amount,
                    pledgeDate,
                    fundId,
                    comments,
                    requestJson,
                    clientMachineName,
                    clientWindowsUser,
                    enqueuedAtUtc,
                    enqueuedAtLocal,
                    tzId,
                    offsetMinutes);

                cmd.Parameters.Add(new SqlParameter("@Id", SqlDbType.Int) { Value = existingId.Value });
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return existingId.Value;
            }
            else
            {
                const string insertSql = @"
INSERT INTO dbo.CGSKYTransactions
(
    WorkflowId,
    TransactionType,
    TransactionStatus,
    StatusNote,
    EnqueuedAtUtc,
    EnqueuedAtLocal,
    EnqueuedLocalTimeZoneId,
    EnqueuedLocalUtcOffsetMinutes,
    ClientMachineName,
    ClientWindowsUser,
    ConstituentId,
    Amount,
    PledgeDate,
    FundId,
    Comments,
    RequestJson
)
VALUES
(
    @WorkflowId,
    @TransactionType,
    @StatusPending,
    NULL,
    @EnqueuedAtUtc,
    @EnqueuedAtLocal,
    @EnqueuedLocalTimeZoneId,
    @EnqueuedLocalUtcOffsetMinutes,
    @ClientMachineName,
    @ClientWindowsUser,
    @ConstituentId,
    @Amount,
    @PledgeDate,
    @FundId,
    @Comments,
    @RequestJson
);
SELECT CAST(SCOPE_IDENTITY() AS int);
";

                await using var cmd = new SqlCommand(insertSql, conn, tx);
                AddCommonParams(
                    cmd,
                    workflowId,
                    constituentId,
                    amount,
                    pledgeDate,
                    fundId,
                    comments,
                    requestJson,
                    clientMachineName,
                    clientWindowsUser,
                    enqueuedAtUtc,
                    enqueuedAtLocal,
                    tzId,
                    offsetMinutes);

                cmd.Parameters.Add(new SqlParameter("@TransactionType", SqlDbType.NVarChar, 50) { Value = TransactionTypePledgeCreate });
                var idObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var id = Convert.ToInt32(idObj);

                await tx.CommitAsync(ct).ConfigureAwait(false);
                return id;
            }
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    private static void AddCommonParams(
        SqlCommand cmd,
        Guid workflowId,
        int constituentId,
        decimal amount,
        DateTime pledgeDate,
        string fundId,
        string? comments,
        string requestJson,
        string clientMachineName,
        string clientWindowsUser,
        DateTime enqueuedAtUtc,
        DateTime enqueuedAtLocal,
        string timeZoneId,
        int utcOffsetMinutes)
    {
        cmd.Parameters.Add(new SqlParameter("@WorkflowId", SqlDbType.UniqueIdentifier) { Value = workflowId });
        cmd.Parameters.Add(new SqlParameter("@ConstituentId", SqlDbType.Int) { Value = constituentId });

        cmd.Parameters.Add(new SqlParameter("@Amount", SqlDbType.Decimal) { Precision = 18, Scale = 2, Value = amount });
        cmd.Parameters.Add(new SqlParameter("@PledgeDate", SqlDbType.Date) { Value = pledgeDate.Date });
        cmd.Parameters.Add(new SqlParameter("@FundId", SqlDbType.NVarChar, 50) { Value = fundId.Trim() });
        cmd.Parameters.Add(new SqlParameter("@Comments", SqlDbType.NVarChar, 2000) { Value = (object?)comments ?? DBNull.Value });

        cmd.Parameters.Add(new SqlParameter("@RequestJson", SqlDbType.NVarChar, -1) { Value = requestJson });

        cmd.Parameters.Add(new SqlParameter("@EnqueuedAtUtc", SqlDbType.DateTime2) { Value = enqueuedAtUtc });
        cmd.Parameters.Add(new SqlParameter("@EnqueuedAtLocal", SqlDbType.DateTime2) { Value = enqueuedAtLocal });
        cmd.Parameters.Add(new SqlParameter("@EnqueuedLocalTimeZoneId", SqlDbType.NVarChar, 100) { Value = timeZoneId });
        cmd.Parameters.Add(new SqlParameter("@EnqueuedLocalUtcOffsetMinutes", SqlDbType.Int) { Value = utcOffsetMinutes });

        cmd.Parameters.Add(new SqlParameter("@ClientMachineName", SqlDbType.NVarChar, 128) { Value = clientMachineName });
        cmd.Parameters.Add(new SqlParameter("@ClientWindowsUser", SqlDbType.NVarChar, 128) { Value = clientWindowsUser });

        cmd.Parameters.Add(new SqlParameter("@StatusPending", SqlDbType.NVarChar, 30) { Value = StatusPending });
    }

    private static string SafeGetLocalTimeZoneId()
    {
        try { return TimeZoneInfo.Local.Id; }
        catch { return "Local"; }
    }

    private static async Task EnsureSchemaPresentAsync(SqlConnection conn, CancellationToken ct)
    {
        // Idempotent create + minimal indexes.
        const string sql = @"
IF OBJECT_ID('dbo.CGSKYTransactions','U') IS NULL
BEGIN
    CREATE TABLE dbo.CGSKYTransactions
    (
        SkyTransactionRecordId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_CGSKYTransactions PRIMARY KEY CLUSTERED,

        WorkflowId uniqueidentifier NOT NULL,

        -- Examples: 'PledgeCreate', future: 'GiftDelete', etc.
        TransactionType nvarchar(50) NOT NULL,

        -- 'Pending' | 'Processing' | 'Succeeded' | 'Failed' (server-side worker controls these values)
        TransactionStatus nvarchar(30) NOT NULL
            CONSTRAINT DF_CGSKYTransactions_Status DEFAULT ('Pending'),

        -- Human-readable notes for admins (server-side worker can populate error details, etc.)
        StatusNote nvarchar(2000) NULL,

        -- When the client queued this transaction
        EnqueuedAtUtc datetime2(0) NOT NULL,
        EnqueuedAtLocal datetime2(0) NOT NULL,
        EnqueuedLocalTimeZoneId nvarchar(100) NOT NULL,
        EnqueuedLocalUtcOffsetMinutes int NOT NULL,

        -- Client audit fields
        ClientMachineName nvarchar(128) NOT NULL,
        ClientWindowsUser nvarchar(128) NOT NULL,

        -- Helpful extracted fields for searching/reporting
        ConstituentId int NOT NULL,
        Amount decimal(18,2) NOT NULL,
        PledgeDate date NOT NULL,
        FundId nvarchar(50) NOT NULL,
        Comments nvarchar(2000) NULL,

        -- The request to be posted by the server-side worker
        RequestJson nvarchar(max) NOT NULL,

        -- Server-side processing fields (worker fills these in)
        ProcessingAttemptCount int NOT NULL
            CONSTRAINT DF_CGSKYTransactions_Attempts DEFAULT (0),
        ProcessingStartedAtUtc datetime2(0) NULL,
        ProcessingStartedAtLocal datetime2(0) NULL,
        ProcessingCompletedAtUtc datetime2(0) NULL,
        ProcessingCompletedAtLocal datetime2(0) NULL,
        LastProcessingAttemptAtUtc datetime2(0) NULL,
        LastProcessingAttemptAtLocal datetime2(0) NULL,
        LastProcessingErrorMessage nvarchar(2000) NULL,
        ProcessedGiftId nvarchar(50) NULL,

        -- General update stamps
        UpdatedAtUtc datetime2(0) NULL,
        UpdatedAtLocal datetime2(0) NULL,
        UpdatedLocalTimeZoneId nvarchar(100) NULL,
        UpdatedLocalUtcOffsetMinutes int NULL
    );

    CREATE UNIQUE INDEX UX_CGSKYTransactions_Workflow_Type
        ON dbo.CGSKYTransactions(WorkflowId, TransactionType);

    CREATE INDEX IX_CGSKYTransactions_Status_EnqueuedAtUtc
        ON dbo.CGSKYTransactions(TransactionStatus, EnqueuedAtUtc);

    CREATE INDEX IX_CGSKYTransactions_Constituent_EnqueuedAtUtc
        ON dbo.CGSKYTransactions(ConstituentId, EnqueuedAtUtc);
END;";

        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
