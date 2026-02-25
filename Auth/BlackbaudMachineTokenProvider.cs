using CheerfulGiverNXT.Data;
using Microsoft.Data.SqlClient;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Auth
{
    public sealed class BlackbaudMachineTokenProvider
    {
        private readonly string _connectionString;
        private readonly SqlBlackbaudSecretStore _store;

        private readonly string _clientId;
        private readonly string? _clientSecret;

        private readonly string _subscriptionKey;
private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

        public BlackbaudMachineTokenProvider(
            string connectionString,
            SqlBlackbaudSecretStore store,
            string clientId,
            string subscriptionKey,
            string? clientSecret = null)
        {
    if (string.IsNullOrWhiteSpace(connectionString))
        throw new ArgumentException("connectionString is blank.", nameof(connectionString));

    if (string.IsNullOrWhiteSpace(clientId))
        throw new ArgumentException("clientId is blank.", nameof(clientId));

    if (string.IsNullOrWhiteSpace(subscriptionKey))
        throw new ArgumentException("subscriptionKey is blank.", nameof(subscriptionKey));

    _connectionString = connectionString;
    _store = store;
    _clientId = clientId.Trim();
    _subscriptionKey = subscriptionKey.Trim();
    _clientSecret = string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret.Trim();
}

        public static string GetMachineSecretKey()
        {
            // Keep it stable and avoid collisions with reserved keys.
            var name = (Environment.MachineName ?? "UNKNOWN").Trim().ToUpperInvariant();
            return $"MACHINE:{name}";
        }

        public string SubscriptionKey => _subscriptionKey;

        /// <summary>
        /// Lightweight, no-network authorization check for this machine.
        /// = Opens the DB
        /// = Reads MACHINE:&lt;MachineName&gt;
        /// = Verifies a refresh token exists and is decryptable on this machine
        ///
        /// This does NOT attempt to refresh tokens or call the Blackbaud API.
        /// </summary>
        public async Task<(bool IsAuthorized, DateTimeOffset? ExpiresAtUtc, string? Scope, string MachineSecretKey, string? Reason)>
            GetThisMachineAuthorizationStateAsync(CancellationToken ct = default)
        {
            var machineKey = GetMachineSecretKey();

            try
            {
                await using var conn = new SqlConnection(_connectionString);
                await conn.OpenAsync(ct).ConfigureAwait(false);

                var row = await _store.GetAsync(conn, machineKey, ct).ConfigureAwait(false);
                if (row is null)
                    return (false, null, null, machineKey, "No token row found (or tokens are not decryptable on this machine)." );

                if (string.IsNullOrWhiteSpace(row.RefreshToken))
                    return (false, row.ExpiresAtUtc == DateTimeOffset.MinValue ? null : row.ExpiresAtUtc, row.Scope, machineKey, "Refresh token is missing.");

                return (true,
                    row.ExpiresAtUtc == DateTimeOffset.MinValue ? null : row.ExpiresAtUtc,
                    string.IsNullOrWhiteSpace(row.Scope) ? null : row.Scope,
                    machineKey,
                    null);
            }
            catch (Exception ex)
            {
                return (false, null, null, machineKey, ex.Message);
            }
        }

        /// <summary>
        /// Returns (AccessToken, SubscriptionKey) for THIS machine.
        /// - Loads machine row by Environment.MachineName
        /// - Uses the subscription key provided to this instance (typically from App.config).
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

                // Subscription key comes from configuration (App.config), not SQL.
                var subscriptionKey = _subscriptionKey;

                if (string.IsNullOrWhiteSpace(subscriptionKey))
                    throw new InvalidOperationException("No subscription key configured.");

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
                    // Keep whatever machine row had; global is read separately.
                    // Coalesce to "" to satisfy nullable analysis and to avoid writing NULL unless you explicitly choose to.
                    SubscriptionKey: ""
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

                // Subscription key is configured outside SQL (App.config). Do not persist it in the per-machine token row.
                var existingSub = "";

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
