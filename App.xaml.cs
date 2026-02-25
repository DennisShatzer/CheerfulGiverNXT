using CheerfulGiverNXT.Auth;
using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.Infrastructure.Logging;
using CheerfulGiverNXT.Infrastructure.Ui;
using CheerfulGiverNXT.Infrastructure.Theming;
using System;
using System.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CheerfulGiverNXT
{
    /// <summary>
    /// App startup wiring:
    /// - App.config contains ONLY bootstrap settings (SQL connection string + non-secrets).
    /// - Tokens live in SQL (DPAPI-encrypted via SqlBlackbaudSecretStore).
    /// - BlackbaudClientId, BlackbaudRedirectUri, BlackbaudScopes, and BlackbaudSubscriptionKey live in App.config (single source of truth).
    /// - One shared HttpClient uses BlackbaudAuthHandler for auto token refresh + subscription key header injection.
    /// </summary>
    public partial class App : Application
    {
        public static SqlBlackbaudSecretStore SecretStore { get; private set; } = null!;
        public static BlackbaudMachineTokenProvider TokenProvider { get; private set; } = null!;

        /// <summary>Shared SKY API HttpClient (auto-auth via handler).</summary>
        public static HttpClient BlackbaudApiHttp { get; private set; } = null!;

        /// <summary>Shared services (use the shared HttpClient).</summary>
        public static RenxtConstituentLookupService ConstituentService { get; private set; } = null!;
        public static RenxtGiftServer GiftService { get; private set; } = null!;

        /// <summary>NEW: workflow persistence into SQL Express (same DB as secrets).</summary>
        public static IGiftWorkflowStore GiftWorkflowStore { get; private set; } = null!;

        /// <summary>
        /// Server-side posting queue.
        /// Client enqueues SKY gift transactions into dbo.CGSKYTransactions instead of posting gifts directly.
        /// </summary>
        public static ISkyTransactionQueue SkyTransactionQueue { get; private set; } = null!;

        /// <summary>Current campaign context (CampaignRecordId source of truth).</summary>
        public static ICampaignContext CampaignContext { get; private set; } = null!;

        /// <summary>Gift matching challenges + anonymous match-gifts.</summary>
        public static IGiftMatchService GiftMatchService { get; private set; } = null!;

        /// <summary>
        /// Semicolon-separated fund tokens configured per campaign (dbo.CGCampaigns.FundList).
        /// If any prior contributed fund matches any token, the donor is NOT a first-time giver.
        /// </summary>
        public static IFundListRules FundListRules { get; private set; } = null!;
        protected override async void OnStartup(StartupEventArgs e)
        {
            this.DispatcherUnhandledException += (_, args) =>
{
    try
    {
        UiError.Show(
            args.Exception,
            title: "Unhandled exception",
            context: "DispatcherUnhandledException",
            message: "An unexpected error occurred. Please attach the log file when reporting the issue.",
            includeExceptionMessage: true);
    }
    catch
    {
        // ignore
    }

    args.Handled = true;
    Shutdown();
};

            base.OnStartup(e);

            try
            {
                // UI-only: load theme resources before any windows are created.
                ThemeManager.Apply(AppTheme.Light);


                var sqlConnStr =
                    ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString
                    ?? throw new InvalidOperationException("Missing connection string 'CheerfulGiver' in App.config.");

                // ClientId is not secret; keep it in App.config.
                var clientId =
                    ConfigurationManager.AppSettings["BlackbaudClientId"]
                    ?? throw new InvalidOperationException("Missing appSetting BlackbaudClientId in App.config.");

                SecretStore = new SqlBlackbaudSecretStore(sqlConnStr);
// Subscription key is not secret, but it IS required to call SKY API.
// Single source of truth = App.config.
var subscriptionKey =
    ConfigurationManager.AppSettings["BlackbaudSubscriptionKey"]
    ?? throw new InvalidOperationException("Missing appSetting BlackbaudSubscriptionKey in App.config.");

// Optional: confidential client secret (only if your Blackbaud app is configured that way).
// Single source of truth = App.config.
var clientSecret = (ConfigurationManager.AppSettings["BlackbaudClientSecret"] ?? "").Trim();

TokenProvider = new BlackbaudMachineTokenProvider(
    connectionString: sqlConnStr,
    store: SecretStore,
    clientId: clientId,
    subscriptionKey: subscriptionKey,
    clientSecret: string.IsNullOrWhiteSpace(clientSecret) ? null : clientSecret);

var handler = new BlackbaudAuthHandler(TokenProvider) { InnerHandler = new HttpClientHandler() };

                BlackbaudApiHttp = new HttpClient(handler)
                {
                    BaseAddress = new Uri("https://api.sky.blackbaud.com/")
                };
                BlackbaudApiHttp.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                ConstituentService = new RenxtConstituentLookupService(BlackbaudApiHttp);
                GiftService = new RenxtGiftServer(BlackbaudApiHttp);

                // NEW: workflow persistence store
                GiftWorkflowStore = new SqlGiftWorkflowStore(sqlConnStr);

                // NEW: SKY transaction queue (server-side worker processes these)
                SkyTransactionQueue = new SqlSkyTransactionQueue(sqlConnStr);

                // NEW: current campaign context
                CampaignContext = new SqlCampaignContext(sqlConnStr);

                // NEW: gift match challenges (local-only; no SKY API calls for matches)
                GiftMatchService = new SqlGiftMatchService(sqlConnStr, CampaignContext, GiftWorkflowStore);

                // NEW: fund tokens configured per-campaign in CGCampaigns.FundList
                FundListRules = new SqlCampaignFundListRules(sqlConnStr, CampaignContext);
                // Show main window (no StartupUri in App.xaml)
                var main = new MainWindow();
                main.Show();
            }
            catch (Exception ex)
            {
                UiError.Show(ex, title: "Startup error", context: "App.OnStartup");
                Shutdown(-1);
            }
        }

        private static async Task EnsureGlobalSubscriptionKeyAsync()
        {
            var existing = await SecretStore.GetGlobalSubscriptionKeyAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(existing))
                return;

            var entered = PromptForSecret(
                "Enter Blackbaud Subscription Key",
                "This app needs your Blackbaud SKY API subscription key to call the API.\n" +
                "It will be stored encrypted (DPAPI) in your local SQL Express database under __GLOBAL__.");

            if (string.IsNullOrWhiteSpace(entered))
                throw new InvalidOperationException("No subscription key stored. Enter it to continue.");

            await SecretStore.SetGlobalSubscriptionKeyAsync(entered.Trim()).ConfigureAwait(true);
        }

        private static async Task<string> EnsureOAuthClientSecretAsync()
        {
            var existing = await SecretStore.GetOAuthClientSecretAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(existing))
                return existing!;

            var entered = PromptForSecret(
                "Enter Blackbaud OAuth Client Secret",
                "To enable 'Authorize this PC once' and automatic token refresh,\n" +
                "this app uses the confidential Authorization Code flow.\n\n" +
                "Enter your Blackbaud application client_secret (Primary application secret).\n" +
                "It will be stored encrypted (DPAPI) in your local SQL Express database.");

            if (string.IsNullOrWhiteSpace(entered))
                throw new InvalidOperationException("No OAuth client_secret stored. Enter it to continue.");

            entered = entered.Trim();
            await SecretStore.SetOAuthClientSecretAsync(entered).ConfigureAwait(true);
            return entered;
        }

        private static string? PromptForSecret(string title, string message)
        {
            var win = new Window
            {
                Title = title,
                Width = 560,
                Height = 210,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Content = BuildSecretPromptContent(message, out PasswordBox pb, out Button okBtn),
                Topmost = true
            };

            win.Loaded += (_, __) => pb.Focus();
            okBtn.Click += (_, __) => win.DialogResult = true;

            var result = win.ShowDialog();
            if (result == true)
                return pb.Password;

            return null;
        }

        private static UIElement BuildSecretPromptContent(string message, out PasswordBox pb, out Button okBtn)
        {
            var root = new Grid { Margin = new Thickness(14) };

            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var text = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(text, 0);
            root.Children.Add(text);

            pb = new PasswordBox { Margin = new Thickness(0, 0, 0, 10) };
            Grid.SetRow(pb, 1);
            root.Children.Add(pb);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            okBtn = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancel);

            Grid.SetRow(btnPanel, 2);
            root.Children.Add(btnPanel);

            return root;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { BlackbaudApiHttp?.Dispose(); } catch { /* ignore */ }
            base.OnExit(e);
        }
    }
}