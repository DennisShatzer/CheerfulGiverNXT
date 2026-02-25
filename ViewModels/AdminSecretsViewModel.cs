using CheerfulGiverNXT.Auth;
using CheerfulGiverNXT.Infrastructure.Configuration;
using Microsoft.Data.SqlClient;
using System;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CheerfulGiverNXT.ViewModels
{
    public sealed class AdminSecretsViewModel : INotifyPropertyChanged
    {
        private readonly string _connectionString;

        public AdminSecretsViewModel()
        {
            _connectionString = ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString
                ?? throw new InvalidOperationException("Missing connection string 'CheerfulGiver' in App.config.");

            MachineName = Environment.MachineName;
            MachineKey = BlackbaudMachineTokenProvider.GetMachineSecretKey();
            DatabaseSummary = BuildDbSummary(_connectionString);

            // defaults (UI-friendly)
            GlobalSubscriptionStatusText = "(loading…)";
            ClientSecretStatusText = "(loading…)";
            MachineTokenStatusText = "(loading…)";
            GlobalSubscriptionUpdatedAtText = "";
            ClientSecretUpdatedAtText = "";
            MachineTokenUpdatedAtText = "";
            MachineTokenExpiresAtText = "";
        }

        public string MachineName { get; }
        public string MachineKey { get; }
        public string DatabaseSummary { get; }

        private bool _hasGlobalSubscription;
        public bool HasGlobalSubscription
        {
            get => _hasGlobalSubscription;
            private set { _hasGlobalSubscription = value; OnPropertyChanged(); }
        }

        private bool _hasClientSecret;
        public bool HasClientSecret
        {
            get => _hasClientSecret;
            private set { _hasClientSecret = value; OnPropertyChanged(); }
        }

        private bool _hasMachineTokens;
        public bool HasMachineTokens
        {
            get => _hasMachineTokens;
            private set { _hasMachineTokens = value; OnPropertyChanged(); }
        }

        private string _globalSubscriptionStatusText = "";
        public string GlobalSubscriptionStatusText
        {
            get => _globalSubscriptionStatusText;
            private set { _globalSubscriptionStatusText = value; OnPropertyChanged(); }
        }

        private string _globalSubscriptionUpdatedAtText = "";
        public string GlobalSubscriptionUpdatedAtText
        {
            get => _globalSubscriptionUpdatedAtText;
            private set { _globalSubscriptionUpdatedAtText = value; OnPropertyChanged(); }
        }

        private string _clientSecretStatusText = "";
        public string ClientSecretStatusText
        {
            get => _clientSecretStatusText;
            private set { _clientSecretStatusText = value; OnPropertyChanged(); }
        }

        private string _clientSecretUpdatedAtText = "";
        public string ClientSecretUpdatedAtText
        {
            get => _clientSecretUpdatedAtText;
            private set { _clientSecretUpdatedAtText = value; OnPropertyChanged(); }
        }

        private string _machineTokenStatusText = "";
        public string MachineTokenStatusText
        {
            get => _machineTokenStatusText;
            private set { _machineTokenStatusText = value; OnPropertyChanged(); }
        }

        private string _machineTokenUpdatedAtText = "";
        public string MachineTokenUpdatedAtText
        {
            get => _machineTokenUpdatedAtText;
            private set { _machineTokenUpdatedAtText = value; OnPropertyChanged(); }
        }

        private string _machineTokenExpiresAtText = "";
        public string MachineTokenExpiresAtText
        {
            get => _machineTokenExpiresAtText;
            private set { _machineTokenExpiresAtText = value; OnPropertyChanged(); }
        }

        public async Task RefreshAsync(CancellationToken ct = default)
        {
            // Read status without decrypting (professional + safe for UI)
            var global = await ReadRowStatusAsync("__GLOBAL__", ct);
            var clientSecret = await ReadRowStatusAsync("__OAUTH_CLIENT_SECRET__", ct);
            var machine = await ReadRowStatusAsync(MachineKey, ct);

            HasGlobalSubscription = global.SubLen > 0;
            GlobalSubscriptionStatusText = HasGlobalSubscription ? "Stored" : "Not stored";
            GlobalSubscriptionUpdatedAtText = FormatUtc(global.UpdatedAtUtc);

            HasClientSecret = clientSecret.SubLen > 0;
            ClientSecretStatusText = HasClientSecret ? "Stored" : "Not stored";
            ClientSecretUpdatedAtText = FormatUtc(clientSecret.UpdatedAtUtc);

            HasMachineTokens = machine.RefreshLen > 0;
            MachineTokenStatusText = HasMachineTokens ? "Authorized" : "Not authorized";
            MachineTokenUpdatedAtText = FormatUtc(machine.UpdatedAtUtc);
            MachineTokenExpiresAtText = FormatUtc(machine.ExpiresAtUtc);
        }

        public async Task SaveSubscriptionKeyAsync(string subscriptionKey, CancellationToken ct = default)
        {
            await App.SecretStore.SetGlobalSubscriptionKeyAsync(subscriptionKey, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        public async Task SaveClientSecretAsync(string clientSecret, CancellationToken ct = default)
        {
            await App.SecretStore.SetOAuthClientSecretAsync(clientSecret, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        public async Task ClearSubscriptionKeyAsync(CancellationToken ct = default)
        {
            await DeleteRowAsync("__GLOBAL__", ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        public async Task ClearClientSecretAsync(CancellationToken ct = default)
        {
            await DeleteRowAsync("__OAUTH_CLIENT_SECRET__", ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        public async Task ClearMachineTokensAsync(CancellationToken ct = default)
        {
            await DeleteRowAsync(MachineKey, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        public async Task AuthorizeThisPcAsync(CancellationToken ct = default)
        {
            // Scopes are configured in App.config (BlackbaudScopes). Do not hard-code them in code.
            await App.TokenProvider.SeedThisMachineAsync(BlackbaudConfig.RedirectUri, BlackbaudConfig.Scopes, ct).ConfigureAwait(false);
            await RefreshAsync(ct).ConfigureAwait(false);
        }

        private async Task DeleteRowAsync(string key, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = new SqlCommand("DELETE FROM dbo.CGOAuthSecrets WHERE SecretKey = @k", conn);
            cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.NVarChar, 128) { Value = key });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        private sealed record RowStatus(int SubLen, int AccessLen, int RefreshLen, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? UpdatedAtUtc);

        private async Task<RowStatus> ReadRowStatusAsync(string key, CancellationToken ct)
        {
            await using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync(ct).ConfigureAwait(false);

            const string sql = @"
SELECT
  SubLen     = ISNULL(DATALENGTH(SubscriptionKeyEnc), 0),
  AccessLen  = ISNULL(DATALENGTH(AccessTokenEnc), 0),
  RefreshLen = ISNULL(DATALENGTH(RefreshTokenEnc), 0),
  ExpiresAtUtc,
  UpdatedAtUtc
FROM dbo.CGOAuthSecrets
WHERE SecretKey = @k;";

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.Add(new SqlParameter("@k", SqlDbType.NVarChar, 128) { Value = key });

            await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await r.ReadAsync(ct).ConfigureAwait(false))
                return new RowStatus(0, 0, 0, null, null);

            var subLen = Convert.ToInt32(r["SubLen"]);
            var accessLen = Convert.ToInt32(r["AccessLen"]);
            var refreshLen = Convert.ToInt32(r["RefreshLen"]);

            DateTimeOffset? expires = null;
            if (r["ExpiresAtUtc"] is DateTime dtExp)
                expires = new DateTimeOffset(DateTime.SpecifyKind(dtExp, DateTimeKind.Utc));

            DateTimeOffset? updated = null;
            if (r["UpdatedAtUtc"] is DateTime dtUpd)
                updated = new DateTimeOffset(DateTime.SpecifyKind(dtUpd, DateTimeKind.Utc));

            return new RowStatus(subLen, accessLen, refreshLen, expires, updated);
        }

        private static string FormatUtc(DateTimeOffset? utc)
        {
            if (utc is null)
                return "";

            // Keep it operator-friendly: show UTC and local time.
            var local = utc.Value.ToLocalTime();
            return $"{utc:yyyy-MM-dd HH:mm:ss}Z  ({local:yyyy-MM-dd HH:mm:ss} local)";
        }

        private static string BuildDbSummary(string connectionString)
        {
            try
            {
                var b = new SqlConnectionStringBuilder(connectionString);
                var server = string.IsNullOrWhiteSpace(b.DataSource) ? "(unknown)" : b.DataSource;
                var db = string.IsNullOrWhiteSpace(b.InitialCatalog) ? "(db?)" : b.InitialCatalog;
                return $"{server} | {db}";
            }
            catch
            {
                return "(unreadable)";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
