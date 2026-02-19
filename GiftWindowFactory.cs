using CheerfulGiverNXT.Services;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT
{
    public sealed class GiftWindowFactory
    {
        /// <summary>
        /// Optional: forces a token/key check (and auto-refresh) so you fail early if not authorized.
        /// </summary>
        public async Task<GiftWindow> CreateAsync(RenxtConstituentLookupService.ConstituentGridRow row, CancellationToken ct = default)
        {
            // This ensures the machine has been authorized (and refreshes token if needed).
            // GiftWindow itself doesn't need tokens; API calls use App.GiftService + auth handler.
            _ = await App.TokenProvider.GetAsync(ct);

            return new GiftWindow(row);
        }
    }
}
