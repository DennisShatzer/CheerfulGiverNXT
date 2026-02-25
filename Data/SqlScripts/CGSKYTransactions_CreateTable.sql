-- ============================================================================
-- dbo.CGSKYTransactions
-- ============================================================================
-- Purpose
--   Client-side CheerfulGiverNXT does NOT post gifts directly to SKY API.
--   Instead, it enqueues JSON requests into this table so a server-side worker
--   can submit them to SKY API.
--
-- Notes
--   - Timestamps are stored as BOTH UTC and local time (plus timezone + offset)
--     for admin quality-of-life.
--   - Enqueue is intended to be idempotent per (WorkflowId, TransactionType).
--   - Server-side worker owns status transitions + processing fields.
-- ============================================================================

IF OBJECT_ID('dbo.CGSKYTransactions','U') IS NULL
BEGIN
    CREATE TABLE dbo.CGSKYTransactions
    (
        SkyTransactionRecordId int IDENTITY(1,1) NOT NULL
            CONSTRAINT PK_CGSKYTransactions PRIMARY KEY CLUSTERED,

        WorkflowId uniqueidentifier NOT NULL,

        -- Examples: 'PledgeCreate', future: 'GiftDelete', etc.
        TransactionType nvarchar(50) NOT NULL,

        -- 'Pending' | 'Processing' | 'Succeeded' | 'Failed'
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
END;
