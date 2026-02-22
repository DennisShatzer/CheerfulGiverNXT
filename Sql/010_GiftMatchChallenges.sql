/*
CheerfulGiverNXT - Gift Match Challenges

What this enables:
- Admin can create multiple anonymous match challenges (Ctrl+Shift+C)
- Challenges are stored in dbo.CGChallenges (ChallengeType = 4, Goal = Budget)
- Allocations are stored in dbo.CGGiftWorkflowChallengeMatches
-- Anonymous match donor constituent id is stored in dbo.CGAppSettings (SettingKey = 'AnonymousMatchConstituentId')
-- (Matching gifts are stored locally; no SKY API gift is created for matches.)

Run this once against the CheerfulGiver SQL database.
*/

SET NOCOUNT ON;
GO

/* 1) Extend challenge type constraints to allow ChallengeType = 4 */
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CGChallenges_Type' AND parent_object_id = OBJECT_ID('dbo.CGChallenges'))
BEGIN
    ALTER TABLE dbo.CGChallenges DROP CONSTRAINT CK_CGChallenges_Type;
END
GO

ALTER TABLE dbo.CGChallenges WITH CHECK
ADD CONSTRAINT CK_CGChallenges_Type
CHECK (
    ChallengeType IN (1,2,3,4)
);
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_CGChallenges_WindowRules' AND parent_object_id = OBJECT_ID('dbo.CGChallenges'))
BEGIN
    ALTER TABLE dbo.CGChallenges DROP CONSTRAINT CK_CGChallenges_WindowRules;
END
GO

/*
Type 1/2: windowed challenges require StartLocal + EndLocalExclusive
Type 3/4: always-on challenges require StartLocal/EndLocalExclusive NULL
*/
ALTER TABLE dbo.CGChallenges WITH CHECK
ADD CONSTRAINT CK_CGChallenges_WindowRules
CHECK (
    (
        (ChallengeType IN (1,2))
        AND StartLocal IS NOT NULL
        AND EndLocalExclusive IS NOT NULL
        AND EndLocalExclusive > StartLocal
    )
    OR
    (
        (ChallengeType IN (3,4))
        AND StartLocal IS NULL
        AND EndLocalExclusive IS NULL
    )
);
GO

/* 2) Match allocation table for workflow-based gifts */
IF OBJECT_ID('dbo.CGGiftWorkflowChallengeMatches','U') IS NULL
BEGIN
    CREATE TABLE dbo.CGGiftWorkflowChallengeMatches
    (
        MatchRecordId        INT IDENTITY(1,1) NOT NULL,
        SourceWorkflowId     UNIQUEIDENTIFIER NOT NULL,
        SourceApiGiftId      NVARCHAR(50) NULL,
        ChallengeRecordId    INT NOT NULL,
        MatchAmount          DECIMAL(18,2) NOT NULL,

        MatchedWorkflowId    UNIQUEIDENTIFIER NULL,
        MatchedApiGiftId     NVARCHAR(50) NULL,

        ApiSucceeded         BIT NOT NULL CONSTRAINT DF_CGGiftWorkflowChallengeMatches_ApiSucceeded DEFAULT (0),
        ApiErrorMessage      NVARCHAR(2000) NULL,

        CreatedAtUtc         DATETIME2(0) NOT NULL CONSTRAINT DF_CGGiftWorkflowChallengeMatches_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),

        CONSTRAINT PK_CGGiftWorkflowChallengeMatches PRIMARY KEY CLUSTERED (MatchRecordId ASC),
        CONSTRAINT CK_CGGiftWorkflowChallengeMatches_NonNegative CHECK (MatchAmount >= (0))
    );

    ALTER TABLE dbo.CGGiftWorkflowChallengeMatches WITH CHECK
    ADD CONSTRAINT FK_CGGiftWorkflowChallengeMatches_CGChallenges
    FOREIGN KEY (ChallengeRecordId)
    REFERENCES dbo.CGChallenges(ChallengeRecordId);

    ALTER TABLE dbo.CGGiftWorkflowChallengeMatches WITH CHECK
    ADD CONSTRAINT FK_CGGiftWorkflowChallengeMatches_SourceWorkflow
    FOREIGN KEY (SourceWorkflowId)
    REFERENCES dbo.CGGiftWorkflows(WorkflowId);

    ALTER TABLE dbo.CGGiftWorkflowChallengeMatches WITH CHECK
    ADD CONSTRAINT FK_CGGiftWorkflowChallengeMatches_MatchedWorkflow
    FOREIGN KEY (MatchedWorkflowId)
    REFERENCES dbo.CGGiftWorkflows(WorkflowId);
END
GO

PRINT 'Gift Match Challenge schema is ready.';
GO
