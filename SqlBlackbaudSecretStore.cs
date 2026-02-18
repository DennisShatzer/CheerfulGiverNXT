using Microsoft.Data.SqlClient;
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT
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
        Task SetGlobalSubscriptionKeyAsync(string subscriptionKey, CancellationToken ct = default);
    }

    public sealed class SqlBlackbaudSecretStore : IBlackbaudSecretStore
    {
        public const string GlobalKey = "__GLOBAL__";

        private readonly string _connectionString;
        private readonly string _passphrase;

        public SqlBlackbaudSecretStore(string connectionString, string passphrase)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _passphrase = passphrase ?? throw new ArgumentNullException(nameof(passphrase));
        }

        public async Task<BlackbaudSecrets?> GetAsync(SqlConnection conn, string secretKey, CancellationToken ct = default)
        {
            await using var cmd = new SqlCommand("dbo.CGOAuthSecrets_Get", conn)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@SecretKey", secretKey);
            cmd.Parameters.AddWithValue("@Passphrase", _passphrase);

            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return null;

            var access = r["AccessToken"] as string;
            var refresh = r["RefreshToken"] as string;
            var tokenType = r["TokenType"] as string ?? "";
            var scope = r["Scope"] as string ?? "";
            var subKey = r["SubscriptionKey"] as string ?? "";

            if (r["ExpiresAtUtc"] is DBNull)
                return null;

            var expiresAtUtc = (DateTime)r["ExpiresAtUtc"];
            var expires = new DateTimeOffset(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc));

            // If this is the GLOBAL row, access/refresh may be null — caller can handle that.
            access ??= "";
            refresh ??= "";

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
            await using var cmd = new SqlCommand("dbo.CGOAuthSecrets_Upsert", conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.AddWithValue("@SecretKey", secretKey);
            cmd.Parameters.AddWithValue("@Passphrase", _passphrase);

            cmd.Parameters.AddWithValue("@AccessToken", string.IsNullOrWhiteSpace(secrets.AccessToken) ? (object)DBNull.Value : secrets.AccessToken);
            cmd.Parameters.AddWithValue("@RefreshToken", string.IsNullOrWhiteSpace(secrets.RefreshToken) ? (object)DBNull.Value : secrets.RefreshToken);
            cmd.Parameters.AddWithValue("@ExpiresAtUtc", secrets.ExpiresAtUtc.UtcDateTime);

            cmd.Parameters.AddWithValue("@TokenType", secrets.TokenType ?? "");
            cmd.Parameters.AddWithValue("@Scope", secrets.Scope ?? "");

            cmd.Parameters.AddWithValue("@SubscriptionKey",
                string.IsNullOrWhiteSpace(secrets.SubscriptionKey) ? (object)DBNull.Value : secrets.SubscriptionKey);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public async Task<string?> GetGlobalSubscriptionKeyAsync(SqlConnection conn, CancellationToken ct = default)
        {
            var global = await GetAsync(conn, GlobalKey, ct).ConfigureAwait(false);
            if (global is null) return null;
            return string.IsNullOrWhiteSpace(global.SubscriptionKey) ? null : global.SubscriptionKey;
        }

        public async Task SetGlobalSubscriptionKeyAsync(string subscriptionKey, CancellationToken ct = default)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            // Only set SubscriptionKey; leave token fields unchanged by passing NULL for them.
            await using var cmd = new SqlCommand("dbo.CGOAuthSecrets_Upsert", conn)
            {
                CommandType = CommandType.StoredProcedure
            };

            cmd.Parameters.AddWithValue("@SecretKey", GlobalKey);
            cmd.Parameters.AddWithValue("@Passphrase", _passphrase);

            cmd.Parameters.AddWithValue("@AccessToken", DBNull.Value);
            cmd.Parameters.AddWithValue("@RefreshToken", DBNull.Value);
            cmd.Parameters.AddWithValue("@ExpiresAtUtc", DBNull.Value);
            cmd.Parameters.AddWithValue("@TokenType", DBNull.Value);
            cmd.Parameters.AddWithValue("@Scope", DBNull.Value);

            cmd.Parameters.AddWithValue("@SubscriptionKey", subscriptionKey);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }
}
