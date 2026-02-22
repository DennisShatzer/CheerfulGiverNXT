using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data;

/// <summary>
/// Source of truth for the app's current CampaignRecordId.
/// This is the CampaignRecordId created/managed by the admin campaign configuration tool.
/// </summary>
public interface ICampaignContext
{
    /// <summary>
    /// Returns the current CampaignRecordId, or null if none can be determined.
    /// </summary>
    Task<int?> GetCurrentCampaignRecordIdAsync(CancellationToken ct = default);
}
