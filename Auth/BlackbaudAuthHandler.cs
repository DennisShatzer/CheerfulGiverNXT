using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Auth
{
    /// <summary>
    /// Injects Bearer token and subscription key into every SKY API request.
    /// Token + key come from BlackbaudMachineTokenProvider (SQL-backed, auto-refresh).
    /// </summary>
    public sealed class BlackbaudAuthHandler : DelegatingHandler
    {
        private readonly BlackbaudMachineTokenProvider _provider;

        public BlackbaudAuthHandler(BlackbaudMachineTokenProvider provider)
        {
            _provider = provider;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (accessToken, subscriptionKey) = await _provider.GetAsync(ct).ConfigureAwait(false);

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Remove any variant, then add one canonical header.
            if (request.Headers.Contains("Bb-Api-Subscription-Key"))
                request.Headers.Remove("Bb-Api-Subscription-Key");
            if (request.Headers.Contains("bb-api-subscription-key"))
                request.Headers.Remove("bb-api-subscription-key");

            request.Headers.Add("Bb-Api-Subscription-Key", subscriptionKey);

            return await base.SendAsync(request, ct).ConfigureAwait(false);
        }
    }
}
