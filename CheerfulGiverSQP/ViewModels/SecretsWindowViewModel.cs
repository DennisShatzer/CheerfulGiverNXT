using System;
using System.ComponentModel;
using System.Configuration;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using CheerfulGiverNXT.Auth;
using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Infrastructure;
using Microsoft.Data.SqlClient;

namespace CheerfulGiverSQP.ViewModels;

public sealed class SecretsWindowViewModel : INotifyPropertyChanged
{
    private readonly SqlBlackbaudSecretStore _store;
    private readonly BlackbaudMachineTokenProvider _provider;

    public SecretsWindowViewModel(
        SqlBlackbaudSecretStore secretStore,
        BlackbaudMachineTokenProvider tokenProvider,
        string clientId,
        string redirectUri,
        string scope)
    {
        _store = secretStore;
        _provider = tokenProvider;

        ClientId = clientId;
        RedirectUri = redirectUri;
        Scope = scope;
        MachineSecretKey = BlackbaudMachineTokenProvider.GetMachineSecretKey();

        // Single source of truth for subscription key and (optional) client secret is App.config.
        SubscriptionKey = _provider.SubscriptionKey;
        ClientSecretStatus = string.IsNullOrWhiteSpace(System.Configuration.ConfigurationManager.AppSettings["BlackbaudClientSecret"])
            ? "Not configured in App.config."
            : "Configured in App.config (not shown).";

        AuthorizeCommand = new AsyncRelayCommand(AuthorizeAsync);
        ReloadStatusCommand = new AsyncRelayCommand(ReloadAsync);
        _ = ReloadAsync();
    }

    public string MachineSecretKey { get; }
    public string ClientId { get; }
    public string RedirectUri { get; }
    public string Scope { get; }

    public string SubscriptionKey { get; }

    public string ClientSecretStatus { get; }

    public string TokenStatusLine
    {
        get => _tokenStatusLine;
        private set { _tokenStatusLine = value; OnPropertyChanged(); }
    }
    private string _tokenStatusLine = "";

    public string LastError
    {
        get => _lastError;
        private set { _lastError = value; OnPropertyChanged(); }
    }
    private string _lastError = "";

    public AsyncRelayCommand AuthorizeCommand { get; }
    public AsyncRelayCommand ReloadStatusCommand { get; }
    private async Task ReloadAsync()
    {
        try
        {
            LastError = "";
            // Token status is stored under MACHINE:<MachineName>
            await using var conn = new SqlConnection(GetConnStr());
            await conn.OpenAsync().ConfigureAwait(false);

            var row = await _store.GetAsync(conn, MachineSecretKey).ConfigureAwait(false);
            if (row is null || string.IsNullOrWhiteSpace(row.RefreshToken))
            {
                TokenStatusLine = "Not authorized. Click 'Authorize this server' to seed tokens for this machine.";
                return;
            }

            TokenStatusLine =
                $"Authorized. ExpiresAtUtc={row.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss}Z, Scope='{row.Scope}'.";
        }
        catch (Exception ex)
        {
            TokenStatusLine = "";
            LastError = ex.Message;
        }
    }

    private async Task AuthorizeAsync()
    {
        try
        {
            LastError = "";

            if (string.IsNullOrWhiteSpace(ClientId) || ClientId.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("BlackbaudClientId is not configured in App.config.");

            if (string.IsNullOrWhiteSpace(RedirectUri))
                throw new InvalidOperationException("BlackbaudRedirectUri is not configured in App.config.");

            if (string.IsNullOrWhiteSpace(Scope))
                throw new InvalidOperationException("BlackbaudScopes is not configured in App.config.");

            // Seed tokens for THIS machine.
            await _provider.SeedThisMachineAsync(
                redirectUri: RedirectUri,
                scope: Scope).ConfigureAwait(false);

            await ReloadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
    }

    private static string GetConnStr()
    {
        var cs = System.Configuration.ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString;
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Missing connection string 'CheerfulGiver' in App.config.");
        return cs!;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
