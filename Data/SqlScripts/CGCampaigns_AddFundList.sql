/*
    CheerfulGiverNXT migration:
    Add dbo.CGCampaigns.FundList

    FundList is a semicolon-separated list of fund tokens.
    First-time giver rule:
      - If the constituent has ANY prior contributed fund that matches ANY token in FundList,
        then they are NOT a first-time giver.
      - If no matches are found, they ARE a first-time giver.

    Safe to run multiple times.
*/

BEGIN TRY
    BEGIN TRAN;

    IF OBJECT_ID('dbo.CGCampaigns', 'U') IS NULL
    BEGIN
        THROW 50000, 'dbo.CGCampaigns does not exist.', 1;
    END

    IF COL_LENGTH('dbo.CGCampaigns', 'FundList') IS NULL
    BEGIN
        ALTER TABLE dbo.CGCampaigns
            ADD FundList nvarchar(max) NOT NULL
                CONSTRAINT DF_CGCampaigns_FundList DEFAULT (N'');
    END

    COMMIT TRAN;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;
    DECLARE @Err nvarchar(4000) = ERROR_MESSAGE();
    RAISERROR('Failed to add dbo.CGCampaigns.FundList: %s', 16, 1, @Err);
END CATCH;
