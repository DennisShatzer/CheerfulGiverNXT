/*
CheerfulGiverNXT migration: make dbo.CGFirstTimeFundExclusions the single source of truth
for "Radio fund" tokens as ONE semicolon-separated record per CampaignRecordId.

What this script does:
1) Preserves your existing first-time giver exclusions list by renaming the legacy
   dbo.CGFirstTimeFundExclusions table to dbo.CGFirstTimeGiverFundExclusions (if needed).
2) Creates (or upgrades) dbo.CGFirstTimeFundExclusions into a small table with ONE row
   per campaign containing a semicolon-separated NVARCHAR(MAX) list of fund tokens.

After running, configure tokens by inserting/updating dbo.CGFirstTimeFundExclusions.FundTokens.
Example:
  UPDATE dbo.CGFirstTimeFundExclusions
  SET FundTokens = N'Radio;WXCR;FM Station', UpdatedAt = SYSUTCDATETIME(), UpdatedBy = SUSER_SNAME()
  WHERE CampaignRecordId = 1;
*/

SET NOCOUNT ON;

-- 1) Preserve the legacy first-time-giver exclusions table under a new name.
IF OBJECT_ID('dbo.CGFirstTimeGiverFundExclusions', 'U') IS NULL
BEGIN
    IF OBJECT_ID('dbo.CGFirstTimeFundExclusions', 'U') IS NOT NULL
    BEGIN
        -- Only rename if the legacy table looks like the old row-per-token schema.
        IF COL_LENGTH('dbo.CGFirstTimeFundExclusions', 'FundExclusionId') IS NOT NULL
           AND COL_LENGTH('dbo.CGFirstTimeFundExclusions', 'FundName') IS NOT NULL
        BEGIN
            EXEC sp_rename 'dbo.CGFirstTimeFundExclusions', 'CGFirstTimeGiverFundExclusions';
        END
    END
END

-- 2) Ensure dbo.CGFirstTimeFundExclusions exists in the new "single record" format.
IF OBJECT_ID('dbo.CGFirstTimeFundExclusions', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.CGFirstTimeFundExclusions
    (
        CampaignRecordId INT NOT NULL,
        FundTokens NVARCHAR(MAX) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_FundTokens DEFAULT (N''),
        UpdatedAt DATETIME2(7) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedBy NVARCHAR(200) NULL,
        CONSTRAINT PK_CGFirstTimeFundExclusions PRIMARY KEY CLUSTERED (CampaignRecordId)
    );
END
ELSE
BEGIN
    -- If the table already exists, make sure the required columns exist.
    IF COL_LENGTH('dbo.CGFirstTimeFundExclusions', 'FundTokens') IS NULL
    BEGIN
        ALTER TABLE dbo.CGFirstTimeFundExclusions
            ADD FundTokens NVARCHAR(MAX) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_FundTokens DEFAULT (N'');
    END

    IF COL_LENGTH('dbo.CGFirstTimeFundExclusions', 'UpdatedAt') IS NULL
    BEGIN
        ALTER TABLE dbo.CGFirstTimeFundExclusions
            ADD UpdatedAt DATETIME2(7) NOT NULL CONSTRAINT DF_CGFirstTimeFundExclusions_UpdatedAt DEFAULT (SYSUTCDATETIME());
    END

    IF COL_LENGTH('dbo.CGFirstTimeFundExclusions', 'UpdatedBy') IS NULL
    BEGIN
        ALTER TABLE dbo.CGFirstTimeFundExclusions
            ADD UpdatedBy NVARCHAR(200) NULL;
    END

    -- If CampaignRecordId is not a PK yet, create a unique PK-like constraint.
    IF NOT EXISTS (
        SELECT 1
        FROM sys.key_constraints kc
        WHERE kc.parent_object_id = OBJECT_ID('dbo.CGFirstTimeFundExclusions')
          AND kc.type = 'PK'
    )
    BEGIN
        -- If duplicates exist, this will fail; clean up duplicates first.
        ALTER TABLE dbo.CGFirstTimeFundExclusions
            ADD CONSTRAINT PK_CGFirstTimeFundExclusions PRIMARY KEY CLUSTERED (CampaignRecordId);
    END
END

PRINT 'CGFirstTimeFundExclusions migration complete.';
