/*
CheerfulGiverNXT - Workflow persistence schema
Run this against the SAME database used by the "CheerfulGiver" connection string in App.config.

Creates 3 tables:
- dbo.CGGiftWorkflows
- dbo.CGGiftWorkflowGifts
- dbo.CGGiftWorkflowSponsorships

Safe to run multiple times.
*/

IF OBJECT_ID('dbo.CGGiftWorkflows','U') IS NULL
BEGIN
    CREATE TABLE dbo.CGGiftWorkflows
    (
        WorkflowId UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_CGGiftWorkflows PRIMARY KEY,
        CreatedAtUtc DATETIME2(0) NOT NULL,
        CompletedAtUtc DATETIME2(0) NULL,

        MachineName NVARCHAR(128) NOT NULL,
        WindowsUser NVARCHAR(128) NOT NULL,

        Status NVARCHAR(30) NOT NULL,
        SearchText NVARCHAR(200) NULL,

        ConstituentId INT NOT NULL,
        ConstituentName NVARCHAR(200) NULL,

        IsFirstTimeGiver BIT NULL,
        IsNewRadioConstituent BIT NULL,

        ContextJson NVARCHAR(MAX) NOT NULL
    );
END
GO

IF OBJECT_ID('dbo.CGGiftWorkflowGifts','U') IS NULL
BEGIN
    CREATE TABLE dbo.CGGiftWorkflowGifts
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CGGiftWorkflowGifts PRIMARY KEY,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        ConstituentId INT NOT NULL,

        Amount DECIMAL(18,2) NOT NULL,
        Frequency NVARCHAR(30) NULL,
        Installments INT NULL,
        PledgeDate DATE NULL,
        StartDate DATE NULL,

        FundId NVARCHAR(50) NULL,
        CampaignId NVARCHAR(50) NULL,
        AppealId NVARCHAR(50) NULL,
        PackageId NVARCHAR(50) NULL,

        SendReminder BIT NOT NULL CONSTRAINT DF_CGGiftWorkflowGifts_SendReminder DEFAULT(0),
        Comments NVARCHAR(2000) NULL,

        ApiAttemptedAtUtc DATETIME2(0) NULL,
        ApiSucceeded BIT NOT NULL CONSTRAINT DF_CGGiftWorkflowGifts_ApiSucceeded DEFAULT(0),
        ApiGiftId NVARCHAR(50) NULL,
        ApiErrorMessage NVARCHAR(2000) NULL,

        CreatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_CGGiftWorkflowGifts_CreatedAtUtc DEFAULT(SYSUTCDATETIME())
    );

    ALTER TABLE dbo.CGGiftWorkflowGifts
        ADD CONSTRAINT FK_CGGiftWorkflowGifts_Workflow
        FOREIGN KEY (WorkflowId) REFERENCES dbo.CGGiftWorkflows(WorkflowId);
END
GO

IF OBJECT_ID('dbo.CGGiftWorkflowSponsorships','U') IS NULL
BEGIN
    CREATE TABLE dbo.CGGiftWorkflowSponsorships
    (
        Id INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CGGiftWorkflowSponsorships PRIMARY KEY,
        WorkflowId UNIQUEIDENTIFIER NOT NULL,
        ConstituentId INT NOT NULL,

        SponsoredDate DATE NOT NULL,
        Slot NVARCHAR(20) NOT NULL,
        ThresholdAmount DECIMAL(18,2) NULL,

        CreatedAtUtc DATETIME2(0) NOT NULL CONSTRAINT DF_CGGiftWorkflowSponsorships_CreatedAtUtc DEFAULT(SYSUTCDATETIME())
    );

    ALTER TABLE dbo.CGGiftWorkflowSponsorships
        ADD CONSTRAINT FK_CGGiftWorkflowSponsorships_Workflow
        FOREIGN KEY (WorkflowId) REFERENCES dbo.CGGiftWorkflows(WorkflowId);
END
GO
