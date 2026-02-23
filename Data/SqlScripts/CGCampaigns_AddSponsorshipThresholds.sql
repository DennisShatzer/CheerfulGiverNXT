/*
    Adds sponsorship threshold amounts to dbo.CGCampaigns.

    These values are used to determine which sponsorship tiers appear in the Gift Entry screen:
      - Half-day AM/PM threshold
      - Full day threshold

    Safe to run multiple times.
*/

SET NOCOUNT ON;

BEGIN TRY
    BEGIN TRAN;

    IF COL_LENGTH('dbo.CGCampaigns', 'SponsorshipHalfDayAmount') IS NULL
    BEGIN
        ALTER TABLE dbo.CGCampaigns
            ADD SponsorshipHalfDayAmount decimal(18,2) NOT NULL
                CONSTRAINT DF_CGCampaigns_SponsorshipHalfDayAmount DEFAULT (1000.00);
    END

    IF COL_LENGTH('dbo.CGCampaigns', 'SponsorshipFullDayAmount') IS NULL
    BEGIN
        ALTER TABLE dbo.CGCampaigns
            ADD SponsorshipFullDayAmount decimal(18,2) NOT NULL
                CONSTRAINT DF_CGCampaigns_SponsorshipFullDayAmount DEFAULT (2000.00);
    END

    -- Ensure existing rows have sane values (in case defaults weren't applied)
    UPDATE dbo.CGCampaigns
    SET SponsorshipHalfDayAmount = CASE WHEN SponsorshipHalfDayAmount <= 0 THEN 1000.00 ELSE SponsorshipHalfDayAmount END,
        SponsorshipFullDayAmount = CASE WHEN SponsorshipFullDayAmount <= 0 THEN 2000.00 ELSE SponsorshipFullDayAmount END
    WHERE SponsorshipHalfDayAmount <= 0 OR SponsorshipFullDayAmount <= 0;

    -- Guard: full-day should not be less than half-day
    UPDATE dbo.CGCampaigns
    SET SponsorshipFullDayAmount = CASE WHEN SponsorshipFullDayAmount < SponsorshipHalfDayAmount THEN SponsorshipHalfDayAmount ELSE SponsorshipFullDayAmount END
    WHERE SponsorshipFullDayAmount < SponsorshipHalfDayAmount;

    COMMIT TRAN;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRAN;

    DECLARE @Err nvarchar(4000) = ERROR_MESSAGE();
    RAISERROR(N'Failed to add sponsorship thresholds to dbo.CGCampaigns. %s', 16, 1, @Err);
END CATCH;
