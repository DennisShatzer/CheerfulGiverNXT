/*
  CheerfulGiverNXT
  Radio Fund Tokens (single-row, semicolon-separated)

  Goal:
    - Make dbo.CGFirstTimeFundExclusions a SIMPLE table that stores ONE record:
        FundTokens = 'Radio;FM;WXYZ'

    - Preserve the existing First-Time Giver exclusions feature by renaming the legacy table:
        dbo.CGFirstTimeFundExclusions  ->  dbo.CGFirstTimeGiverFundExclusions

  This script is designed to be SAFE and idempotent.
*/

SET NOCOUNT ON;

DECLARE @legacyHasCampaignId bit = 0;

IF OBJECT_ID('dbo.CGFirstTimeFundExclusions', 'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'dbo' AND t.name = 'CGFirstTimeFundExclusions' AND c.name = 'CampaignRecordId'
    )
    BEGIN
        SET @legacyHasCampaignId = 1;
    END
END

-- If legacy schema exists under the old name, rename it to preserve behavior.
IF @legacyHasCampaignId = 1
BEGIN
    IF OBJECT_ID('dbo.CGFirstTimeGiverFundExclusions', 'U') IS NULL
    BEGIN
        EXEC sp_rename 'dbo.CGFirstTimeFundExclusions', 'CGFirstTimeGiverFundExclusions';
        PRINT 'Renamed dbo.CGFirstTimeFundExclusions -> dbo.CGFirstTimeGiverFundExclusions (legacy exclusions).';
    END
    ELSE
    BEGIN
        PRINT 'dbo.CGFirstTimeGiverFundExclusions already exists; leaving dbo.CGFirstTimeFundExclusions unchanged.';
    END
END

-- Ensure the new radio-funds table exists with the simple single-row schema.
IF OBJECT_ID('dbo.CGFirstTimeFundExclusions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CGFirstTimeFundExclusions
    (
        Id           int IDENTITY(1,1) NOT NULL CONSTRAINT PK_CGFirstTimeFundExclusions PRIMARY KEY,
        FundTokens   nvarchar(max) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_FundTokens DEFAULT(N''),
        UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedBy    nvarchar(200) NULL
    );

    PRINT 'Created dbo.CGFirstTimeFundExclusions (radio fund tokens single-row table).';
END
ELSE
BEGIN
    -- If the table exists but doesn't have FundTokens (unexpected), add it.
    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'dbo' AND t.name = 'CGFirstTimeFundExclusions' AND c.name = 'FundTokens'
    )
    BEGIN
        ALTER TABLE dbo.CGFirstTimeFundExclusions
            ADD FundTokens nvarchar(max) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_FundTokens2 DEFAULT(N'');

        PRINT 'Added FundTokens column to dbo.CGFirstTimeFundExclusions.';
    END

    IF NOT EXISTS (
        SELECT 1
        FROM sys.columns c
        JOIN sys.tables t ON t.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE s.name = 'dbo' AND t.name = 'CGFirstTimeFundExclusions' AND c.name = 'UpdatedAtUtc'
    )
    BEGIN
        ALTER TABLE dbo.CGFirstTimeFundExclusions
            ADD UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_UpdatedAtUtc2 DEFAULT (SYSUTCDATETIME());

        PRINT 'Added UpdatedAtUtc column to dbo.CGFirstTimeFundExclusions.';
    END
END

-- Ensure at least one row exists.
IF NOT EXISTS (SELECT 1 FROM dbo.CGFirstTimeFundExclusions)
BEGIN
    INSERT INTO dbo.CGFirstTimeFundExclusions (FundTokens, UpdatedBy)
    VALUES (N'', SUSER_SNAME());

    PRINT 'Inserted initial row into dbo.CGFirstTimeFundExclusions.';
END

PRINT 'Done.';
