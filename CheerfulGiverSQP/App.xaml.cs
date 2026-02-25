using System;
using System.Configuration;
using System.IO;
using System.Net.Http;
using System.Windows;
using CheerfulGiverNXT.Auth;
using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Infrastructure.AppMode;
using CheerfulGiverNXT.Services;
using Microsoft.Extensions.Configuration;

namespace CheerfulGiverSQP;

public partial class App : System.Windows.Application
{
    private Tray.TrayIconService? _tray;
    private SkyQueue.ProcessorHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ------------------------------------------------------------
// Configuration (single source of truth = App.config)
// ------------------------------------------------------------
static string RequireConnStr(string name)
{
    var cs = System.Configuration.ConfigurationManager.ConnectionStrings[name]?.ConnectionString;
    if (string.IsNullOrWhiteSpace(cs))
        throw new InvalidOperationException($"Missing connection string '{name}' in App.config.");
    return cs!;
}

static string RequireSetting(string key)
{
    var v = System.Configuration.ConfigurationManager.AppSettings[key];
    if (string.IsNullOrWhiteSpace(v))
        throw new InvalidOperationException($"Missing appSetting '{key}' in App.config.");
    return v.Trim();
}

static string OptionalSetting(string key) =>
    (System.Configuration.ConfigurationManager.AppSettings[key] ?? "").Trim();

var connStr = RequireConnStr("CheerfulGiver");

// Blackbaud SKY API app settings
var clientId = RequireSetting("BlackbaudClientId");
var redirectUri = RequireSetting("BlackbaudRedirectUri");
var scope = RequireSetting("BlackbaudScopes");
var subscriptionKey = RequireSetting("BlackbaudSubscriptionKey");

// Optional confidential client secret (only if your Blackbaud app uses it)
var clientSecret = OptionalSetting("BlackbaudClientSecret");

// Optional label stored into StatusNote when claims begin (required here to avoid hidden defaults)
var workerLabel = RequireSetting("WorkerLabel");

var jsonCfg = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .Build();

var options = SkyQueue.ProcessingOptions.FromConfiguration(jsonCfg);


        // ------------------------------------------------------------
        // Shared SKY API auth stack (same as client)
        // ------------------------------------------------------------
        var secretStore = new SqlBlackbaudSecretStore(connStr);

        var tokenProvider = new BlackbaudMachineTokenProvider(
            connectionString: connStr,
            store: secretStore,
            clientId: clientId,
            subscriptionKey: subscriptionKey,
            clientSecret: string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret);

        var authHandler = new BlackbaudAuthHandler(tokenProvider)
        {
            InnerHandler = new HttpClientHandler()
        };

        var http = new HttpClient(authHandler)
        {
            BaseAddress = new Uri("https://api.sky.blackbaud.com/")
        };

        var skyGiftServer = new RenxtGiftServer(http);

        // ------------------------------------------------------------
        // Queue processing engine
        // ------------------------------------------------------------
        var repo = new SkyQueue.SqlSkyTransactionRepository(connStr);
        var processor = new SkyQueue.SkyTransactionProcessor(
            repository: repo,
            skyGiftServer: skyGiftServer,
            workerLabel: workerLabel,
            options: options);

        Func<System.Threading.CancellationToken, System.Threading.Tasks.Task> preflight = async ct =>
        {
            if (!SkyPostingPolicy.IsPostingAllowed(out var reason))
                throw new InvalidOperationException(reason ?? "SKY API posting is disabled.");

            if (string.IsNullOrWhiteSpace(clientId) || clientId.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("BlackbaudClientId is not configured in CheerfulGiverSQP\\App.config.");

            // Validate tokens + subscription key before we start claiming rows.
            await tokenProvider.GetAsync(ct).ConfigureAwait(false);
        };

        _host = new SkyQueue.ProcessorHost(processor, options, preflight);

        // ------------------------------------------------------------
        // UI
        // ------------------------------------------------------------
        var vm = new ViewModels.MainWindowViewModel(
            host: _host,
            repository: repo,
            secretStore: secretStore,
            tokenProvider: tokenProvider,
            clientId: clientId,
            redirectUri: redirectUri,
            scope: scope,
            options: options);

        var win = new Views.MainWindow
        {
            DataContext = vm
        };

        win.Closed += (_, _) =>
        {
            // MainWindow overrides OnClosing to cancel the close and hide to tray when
            // MinimizeToTray is enabled. So if we reach Closed, the window is truly
            // closing and we should exit the app (ShutdownMode is OnExplicitShutdown).
            Shutdown();
        };

        _tray = new Tray.TrayIconService(win, vm);

        // First show
        win.Show();

        // Optional: start minimized-to-tray
        if (vm.StartMinimized)
            _tray.HideToTray();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _host?.Dispose();
            _tray?.Dispose();
        }
        catch
        {
            // Swallow any disposal errors on shutdown.
        }

        base.OnExit(e);
    }
}
