USE [CheerfulGiver]
GO

-- Enforce one active reservation per (CampaignRecordId, SponsoredDate, DayPart).
-- This prevents accidental double-booking of the same slot.
IF OBJECT_ID('dbo.CGDatesSponsored','U') IS NOT NULL
AND NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'UX_CGDatesSponsored_ActiveSlot'
      AND object_id = OBJECT_ID('dbo.CGDatesSponsored')
)
BEGIN
    CREATE UNIQUE INDEX UX_CGDatesSponsored_ActiveSlot
    ON dbo.CGDatesSponsored (CampaignRecordId, SponsoredDate, DayPart)
    WHERE IsCancelled = 0;
END
GO
