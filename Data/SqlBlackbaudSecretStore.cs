using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.Data
{
    public sealed record BlackbaudSecrets(
        string AccessToken,
        string RefreshToken,
        DateTimeOffset ExpiresAtUtc,
        string TokenType,
        string Scope,
        string SubscriptionKey);

    public interface IBlackbaudSecretStore
    {
        Task<BlackbaudSecrets?> GetAsync(SqlConnection conn, string secretKey, CancellationToken ct = default);
        Task UpsertAsync(SqlConnection conn, string secretKey, BlackbaudSecrets secrets, CancellationToken ct = default);

        Task<string?> GetGlobalSubscriptionKeyAsync(SqlConnection conn, CancellationToken ct = default);
        Task<string?> GetGlobalSubscriptionKeyAsync(CancellationToken ct = default);
        Task SetGlobalSubscriptionKeyAsync(string subscriptionKey, CancellationToken ct = default);

        Task<string?> GetOAuthClientSecretAsync(CancellationToken ct = default);
        Task SetOAuthClientSecretAsync(string clientSecret, CancellationToken ct = default);
    }

    /// <summary>
    /// SQL-backed secret store for Blackbaud tokens and subscription key.
    /// This version stores DPAPI-encrypted blobs in CGOAuthSecrets.*Enc columns and DOES NOT require any passphrase.
    ///
    /// Requires stored procs:
    /// - dbo.CGOAuthSecrets_Get2
    /// - dbo.CGOAuthSecrets_Upsert2
    /// </summary>
    public sealed class SqlBlackbaudSecretStore : IBlackbaudSecretStore
    {
        public const string GlobalKey = "__GLOBAL__";
        private const string OAuthClientSecretKey = "__OAUTH_CLIENT_SECRET__";

        private const string GetProc = "dbo.CGOAuthSecrets_Get2";
        private const string UpsertProc = "dbo.CGOAuthSecrets_Upsert2";

        // Stable entropy so old records remain decryptable on this machine.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CheerfulGiverNXT|CGOAuthSecrets|v2");

        private readonly string _connectionString;

        // Use LocalMachine so any Windows user on THIS PC can run the app (fits "Authorize this PC").
        // If you want per-user isolation, switch to DataProtectionScope.CurrentUser.
        private const DataProtectionScope Scope = DataProtectionScope.LocalMachine;

        public SqlBlackbaudSecretStore(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        private static byte[]? ProtectString(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var bytes = Encoding.UTF8.GetBytes(value);
            return ProtectedData.Protect(bytes, Entropy, Scope);
        }

        private static string? UnprotectString(byte[]? value)
        {
            if (value is null || value.Length == 0) return null;
            var bytes = ProtectedData.Unprotect(value, Entropy, Scope);
            return Encoding.UTF8.GetString(bytes);
        }

        public async Task<BlackbaudSecrets?> GetAsync(SqlConnection conn, string secretKey, CancellationToken ct = default)
        {
            await using var cmd = new SqlCommand(GetProc, conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add(new SqlParameter("@SecretKey", SqlDbType.NVarChar, 128) { Value = secretKey });

            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return null;

            byte[]? accessEnc = r["AccessTokenEnc"] is DBNull ? null : (byte[]?)r["AccessTokenEnc"];
            byte[]? refreshEnc = r["RefreshTokenEnc"] is DBNull ? null : (byte[]?)r["RefreshTokenEnc"];
            byte[]? subKeyEnc = r["SubscriptionKeyEnc"] is DBNull ? null : (byte[]?)r["SubscriptionKeyEnc"];

            var tokenType = r["TokenType"] as string ?? "";
            var scope = r["Scope"] as string ?? "";

            DateTimeOffset expires = DateTimeOffset.MinValue;
            if (!(r["ExpiresAtUtc"] is DBNull))
            {
                var expiresAtUtc = (DateTime)r["ExpiresAtUtc"];
                expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc));
            }

            // NOTE: Global row can have null ExpiresAtUtc + no tokens. We still return it so callers can read SubscriptionKey.
            string access, refresh, subKey;
            try
            {
                access = UnprotectString(accessEnc) ?? "";
                refresh = UnprotectString(refreshEnc) ?? "";
                subKey = UnprotectString(subKeyEnc) ?? "";
            }
            catch (CryptographicException)
            {
                // If the table still contains old SQL ENCRYPTBYPASSPHRASE data (or the DB was restored to a different PC),
                // DPAPI decryption will fail. Treat as missing so the operator can re-seed (subscription key + re-authorize PC).
                return null;
            }

            return new BlackbaudSecrets(
                AccessToken: access,
                RefreshToken: refresh,
                ExpiresAtUtc: expires,
                TokenType: tokenType,
                Scope: scope,
                SubscriptionKey: subKey);
        }

        public async Task UpsertAsync(SqlConnection conn, string secretKey, BlackbaudSecrets secrets, CancellationToken ct = default)
        {
            await using var cmd = new SqlCommand(UpsertProc, conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add(new SqlParameter("@SecretKey", SqlDbType.NVarChar, 128) { Value = secretKey });

            var accessEnc = ProtectString(secrets.AccessToken);
            var refreshEnc = ProtectString(secrets.RefreshToken);
            var subKeyEnc = ProtectString(secrets.SubscriptionKey);

            cmd.Parameters.Add(new SqlParameter("@AccessTokenEnc", SqlDbType.VarBinary, -1) { Value = (object?)accessEnc ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@RefreshTokenEnc", SqlDbType.VarBinary, -1) { Value = (object?)refreshEnc ?? DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@ExpiresAtUtc", SqlDbType.DateTime2) { Value = secrets.ExpiresAtUtc == DateTimeOffset.MinValue ? (object)DBNull.Value : secrets.ExpiresAtUtc.UtcDateTime });
            cmd.Parameters.Add(new SqlParameter("@TokenType", SqlDbType.NVarChar, 50) { Value = (object?)(secrets.TokenType ?? "") ?? "" });
            cmd.Parameters.Add(new SqlParameter("@Scope", SqlDbType.NVarChar, 4000) { Value = (object?)(secrets.Scope ?? "") ?? "" });
            cmd.Parameters.Add(new SqlParameter("@SubscriptionKeyEnc", SqlDbType.VarBinary, -1) { Value = (object?)subKeyEnc ?? DBNull.Value });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<string?> GetGlobalSubscriptionKeyAsync(SqlConnection conn, CancellationToken ct = default)
        {
            var global = await GetAsync(conn, GlobalKey, ct).ConfigureAwait(false);
            if (global is null) return null;
            return string.IsNullOrWhiteSpace(global.SubscriptionKey) ? null : global.SubscriptionKey;
        }

        public async Task<string?> GetGlobalSubscriptionKeyAsync(CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return await GetGlobalSubscriptionKeyAsync(conn, ct).ConfigureAwait(false);
        }

        public async Task SetGlobalSubscriptionKeyAsync(string subscriptionKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(subscriptionKey))
                throw new ArgumentException("Subscription key is required.", nameof(subscriptionKey));

            await SetSingleSecretInSubscriptionColumnAsync(GlobalKey, subscriptionKey.Trim(), ct).ConfigureAwait(false);
        }

        public async Task<string?> GetOAuthClientSecretAsync(CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            var row = await GetAsync(conn, OAuthClientSecretKey, ct).ConfigureAwait(false);
            if (row is null) return null;
            return string.IsNullOrWhiteSpace(row.SubscriptionKey) ? null : row.SubscriptionKey;
        }

        public async Task SetOAuthClientSecretAsync(string clientSecret, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(clientSecret))
                throw new ArgumentException("client_secret is required.", nameof(clientSecret));

            await SetSingleSecretInSubscriptionColumnAsync(OAuthClientSecretKey, clientSecret.Trim(), ct).ConfigureAwait(false);
        }

        private async Task SetSingleSecretInSubscriptionColumnAsync(string key, string value, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = new SqlCommand(UpsertProc, conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.Add(new SqlParameter("@SecretKey", SqlDbType.NVarChar, 128) { Value = key });

            // Leave token fields unchanged by passing NULL for them.
            cmd.Parameters.Add(new SqlParameter("@AccessTokenEnc", SqlDbType.VarBinary, -1) { Value = DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@RefreshTokenEnc", SqlDbType.VarBinary, -1) { Value = DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@ExpiresAtUtc", SqlDbType.DateTime2) { Value = DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@TokenType", SqlDbType.NVarChar, 50) { Value = DBNull.Value });
            cmd.Parameters.Add(new SqlParameter("@Scope", SqlDbType.NVarChar, 4000) { Value = DBNull.Value });

            var enc = ProtectString(value);
            cmd.Parameters.Add(new SqlParameter("@SubscriptionKeyEnc", SqlDbType.VarBinary, -1) { Value = (object?)enc ?? DBNull.Value });

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
