/*
    Adds soft-delete support for locally stored pledge workflows.

    - Adds the following columns to dbo.CGGiftWorkflowGifts (safe/idempotent):
        IsDeleted (bit, NOT NULL, default 0)
        DeletedAtUtc (datetime2)
        DeletedByUser (nvarchar)
        DeletedByMachine (nvarchar)
        DeleteReason (nvarchar)
        ApiDeleteAttemptedAtUtc (datetime2)
        ApiDeleteSucceeded (bit)
        ApiDeleteErrorMessage (nvarchar)

    - Creates dbo.CGDeletedPledges (safe/idempotent) as an audit log snapshot table.

    Notes:
      - This supports the LocalTransactionsWindow admin "Delete" action.
      - The application also includes an in-app schema upgrader that performs these changes automatically
        when needed, but this script is provided for DBAs and manual deployments.

    Safe to run multiple times.
*/

BEGIN TRY
    IF OBJECT_ID('dbo.CGGiftWorkflowGifts', 'U') IS NULL
        THROW 50000, 'dbo.CGGiftWorkflowGifts does not exist. Run the CheerfulGiver schema script first.', 1;

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'IsDeleted') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts
            ADD IsDeleted bit NOT NULL
                CONSTRAINT DF_CGGiftWorkflowGifts_IsDeleted DEFAULT (0);
        PRINT 'Added dbo.CGGiftWorkflowGifts.IsDeleted (default 0).';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.IsDeleted already exists; no change.';

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'DeletedAtUtc') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts ADD DeletedAtUtc datetime2(0) NULL;
        PRINT 'Added dbo.CGGiftWorkflowGifts.DeletedAtUtc.';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.DeletedAtUtc already exists; no change.';

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'DeletedByUser') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts ADD DeletedByUser nvarchar(128) NULL;
        PRINT 'Added dbo.CGGiftWorkflowGifts.DeletedByUser.';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.DeletedByUser already exists; no change.';

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'DeletedByMachine') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts ADD DeletedByMachine nvarchar(128) NULL;
        PRINT 'Added dbo.CGGiftWorkflowGifts.DeletedByMachine.';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.DeletedByMachine already exists; no change.';

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'DeleteReason') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts ADD DeleteReason nvarchar(400) NULL;
        PRINT 'Added dbo.CGGiftWorkflowGifts.DeleteReason.';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.DeleteReason already exists; no change.';

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'ApiDeleteAttemptedAtUtc') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts ADD ApiDeleteAttemptedAtUtc datetime2(0) NULL;
        PRINT 'Added dbo.CGGiftWorkflowGifts.ApiDeleteAttemptedAtUtc.';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.ApiDeleteAttemptedAtUtc already exists; no change.';

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'ApiDeleteSucceeded') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts ADD ApiDeleteSucceeded bit NULL;
        PRINT 'Added dbo.CGGiftWorkflowGifts.ApiDeleteSucceeded.';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.ApiDeleteSucceeded already exists; no change.';

    IF COL_LENGTH('dbo.CGGiftWorkflowGifts', 'ApiDeleteErrorMessage') IS NULL
    BEGIN
        ALTER TABLE dbo.CGGiftWorkflowGifts ADD ApiDeleteErrorMessage nvarchar(2000) NULL;
        PRINT 'Added dbo.CGGiftWorkflowGifts.ApiDeleteErrorMessage.';
    END
    ELSE PRINT 'dbo.CGGiftWorkflowGifts.ApiDeleteErrorMessage already exists; no change.';

    IF OBJECT_ID('dbo.CGDeletedPledges', 'U') IS NULL
    BEGIN
        CREATE TABLE dbo.CGDeletedPledges
        (
            DeletedPledgeId int IDENTITY(1,1) NOT NULL
                CONSTRAINT PK_CGDeletedPledges PRIMARY KEY CLUSTERED,

            WorkflowId uniqueidentifier NOT NULL,
            GiftRowId int NULL,

            WorkflowCreatedAtUtc datetime2(0) NOT NULL,
            WorkflowCompletedAtUtc datetime2(0) NULL,
            WorkflowStatus nvarchar(30) NOT NULL,
            WorkflowMachineName nvarchar(128) NOT NULL,
            WorkflowWindowsUser nvarchar(128) NOT NULL,
            SearchText nvarchar(200) NULL,
            ConstituentId int NOT NULL,
            ConstituentName nvarchar(200) NULL,
            IsFirstTimeGiver bit NULL,
            IsNewRadioConstituent bit NULL,
            ContextJson nvarchar(max) NOT NULL,

            Amount decimal(18,2) NULL,
            Frequency nvarchar(30) NULL,
            Installments int NULL,
            PledgeDate date NULL,
            StartDate date NULL,
            FundId nvarchar(50) NULL,
            CampaignId nvarchar(50) NULL,
            AppealId nvarchar(50) NULL,
            PackageId nvarchar(50) NULL,
            SendReminder bit NULL,
            Comments nvarchar(2000) NULL,

            ApiAttemptedAtUtc datetime2(0) NULL,
            ApiSucceeded bit NULL,
            ApiGiftId nvarchar(50) NULL,
            ApiErrorMessage nvarchar(2000) NULL,

            SponsoredDate date NULL,
            Slot nvarchar(20) NULL,
            ThresholdAmount decimal(18,2) NULL,

            DeletedAtUtc datetime2(0) NOT NULL,
            DeletedByMachine nvarchar(128) NOT NULL,
            DeletedByUser nvarchar(128) NOT NULL,
            DeletedReason nvarchar(400) NULL,

            ApiDeleteAttemptedAtUtc datetime2(0) NULL,
            ApiDeleteSucceeded bit NULL,
            ApiDeleteErrorMessage nvarchar(2000) NULL,

            LoggedAtUtc datetime2(0) NOT NULL
        );

        CREATE UNIQUE INDEX UX_CGDeletedPledges_WorkflowId
            ON dbo.CGDeletedPledges(WorkflowId);

        PRINT 'Created dbo.CGDeletedPledges.';
    END
    ELSE
    BEGIN
        PRINT 'dbo.CGDeletedPledges already exists; no change.';
    END
END TRY
BEGIN CATCH
    DECLARE @Err nvarchar(4000) = ERROR_MESSAGE();
    RAISERROR(N'Failed to add workflow soft-delete support: %s', 16, 1, @Err);
END CATCH
