/*
    Adds dbo.CGCampaigns.AnonymousGiverId

    Purpose:
      Per-campaign placeholder constituent ID used when a caller wants to give anonymously.
      This does NOT affect matching-gift anonymous donor settings (those remain separate).

    Safe to run multiple times.
*/

BEGIN TRY
    IF OBJECT_ID('dbo.CGCampaigns', 'U') IS NULL
        THROW 50000, 'dbo.CGCampaigns does not exist.', 1;

    IF COL_LENGTH('dbo.CGCampaigns', 'AnonymousGiverId') IS NULL
    BEGIN
        ALTER TABLE dbo.CGCampaigns
            ADD AnonymousGiverId int NULL;

        PRINT 'Added dbo.CGCampaigns.AnonymousGiverId (NULL).';
    END
    ELSE
    BEGIN
        PRINT 'dbo.CGCampaigns.AnonymousGiverId already exists; no change.';
    END
END TRY
BEGIN CATCH
    DECLARE @Err nvarchar(4000) = ERROR_MESSAGE();
    RAISERROR(N'Failed to add dbo.CGCampaigns.AnonymousGiverId: %s', 16, 1, @Err);
END CATCH
