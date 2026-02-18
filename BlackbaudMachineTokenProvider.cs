using Microsoft.Data.SqlClient;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT
{
    public sealed class BlackbaudMachineTokenProvider
    {
        private readonly string _connectionString;
        private readonly SqlBlackbaudSecretStore _store;

        private readonly string _clientId;
        private readonly string? _clientSecret;

        private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

        public BlackbaudMachineTokenProvider(
            string connectionString,
            SqlBlackbaudSecretStore store,
            string clientId,
            string? clientSecret = null)
        {
            _connectionString = connectionString;
            _store = store;
            _clientId = clientId;
            _clientSecret = clientSecret;
        }

        public static string GetMachineSecretKey()
        {
            // Keep it stable and avoid collisions with reserved keys.
            var name = (Environment.MachineName ?? "UNKNOWN").Trim().ToUpperInvariant();
            return $"MACHINE:{name}";
        }

        /// <summary>
        /// Returns (AccessToken, SubscriptionKey) for THIS machine.
        /// - Loads machine row by Environment.MachineName
        /// - Loads subscription key from __GLOBAL__ (preferred) and falls back to machine row if you choose to store it there.
        /// - Refreshes when expiring soon and persists rotated refresh token.
        /// </summary>
        public async Task<(string AccessToken, string SubscriptionKey)> GetAsync(CancellationToken ct = default)
        {
            var machineKey = GetMachineSecretKey();
            var lockName = $"CGOAuthRefresh:{machineKey}";

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await SqlAppLock.AcquireAsync(conn, lockName, ct).ConfigureAwait(false);
            try
            {
                var machine = await _store.GetAsync(conn, machineKey, ct).ConfigureAwait(false);
                if (machine is null || string.IsNullOrWhiteSpace(machine.RefreshToken))
                {
                    throw new InvalidOperationException(
                        $"No tokens found for {machineKey}. Run interactive login ON THIS MACHINE once to seed tokens.");
                }

                // Get subscription key (global recommended)
                var globalSubKey = await _store.GetGlobalSubscriptionKeyAsync(conn, ct).ConfigureAwait(false);
                var subscriptionKey = !string.IsNullOrWhiteSpace(globalSubKey)
                    ? globalSubKey!
                    : (machine.SubscriptionKey ?? "");

                if (string.IsNullOrWhiteSpace(subscriptionKey))
                    throw new InvalidOperationException("No subscription key stored. Set it in __GLOBAL__ (recommended).");

                // Still valid?
                if (DateTimeOffset.UtcNow < machine.ExpiresAtUtc - RefreshSkew && !string.IsNullOrWhiteSpace(machine.AccessToken))
                    return (machine.AccessToken, subscriptionKey);

                // Refresh (do NOT preserve; we want rotation + persistence)
                var refreshed = await BlackbaudPkceAuthHttps.RefreshTokenAsync(
                    clientId: _clientId,
                    clientSecret: _clientSecret,
                    refreshToken: machine.RefreshToken,
                    preserveRefreshToken: false,
                    ct: ct).ConfigureAwait(false);

                var updated = new BlackbaudSecrets(
                    AccessToken: refreshed.AccessToken,
                    RefreshToken: refreshed.RefreshToken ?? machine.RefreshToken, // safety
                    ExpiresAtUtc: refreshed.ExpiresAtUtc,
                    TokenType: refreshed.TokenType,
                    Scope: refreshed.Scope,
                    SubscriptionKey: machine.SubscriptionKey // keep whatever machine row had; global is read separately
                );

                await _store.UpsertAsync(conn, machineKey, updated, ct).ConfigureAwait(false);

                return (updated.AccessToken, subscriptionKey);
            }
            finally
            {
                await SqlAppLock.ReleaseAsync(conn, lockName, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Seed THIS machine's tokens by doing an interactive PKCE login and saving the result under MACHINE:<MachineName>.
        /// Run once per operator PC.
        /// </summary>
        public async Task SeedThisMachineAsync(
            string redirectUri,
            string scope,
            CancellationToken ct = default)
        {
            var machineKey = GetMachineSecretKey();
            var lockName = $"CGOAuthRefresh:{machineKey}";

            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await SqlAppLock.AcquireAsync(conn, lockName, ct).ConfigureAwait(false);
            try
            {
                var token = await BlackbaudPkceAuthHttps.AcquireTokenAsync(
                    clientId: _clientId,
                    clientSecret: _clientSecret,
                    redirectUri: redirectUri,
                    scope: scope,
                    timeout: TimeSpan.FromMinutes(10),
                    ct: ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(token.RefreshToken))
                    throw new InvalidOperationException("No refresh token returned. Check your Blackbaud app configuration/scopes.");

                // Preserve existing subscription key stored on machine row (if any)
                var existing = await _store.GetAsync(conn, machineKey, ct).ConfigureAwait(false);
                var existingSub = existing?.SubscriptionKey ?? "";

                var secrets = new BlackbaudSecrets(
                    AccessToken: token.AccessToken,
                    RefreshToken: token.RefreshToken!,
                    ExpiresAtUtc: token.ExpiresAtUtc,
                    TokenType: token.TokenType,
                    Scope: token.Scope,
                    SubscriptionKey: existingSub
                );

                await _store.UpsertAsync(conn, machineKey, secrets, ct).ConfigureAwait(false);
            }
            finally
            {
                await SqlAppLock.ReleaseAsync(conn, lockName, ct).ConfigureAwait(false);
            }
        }
    }
}
