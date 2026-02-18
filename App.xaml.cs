using System;
using System.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Windows;

namespace CheerfulGiverNXT
{
    /// <summary>
    /// App startup wiring:
    /// Step 1: Ensure global subscription key exists in SQL.
    /// Step 3: Create ONE SKY API HttpClient with BlackbaudAuthHandler (auto token refresh + key).
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

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                var sqlConnStr = ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString
                    ?? throw new InvalidOperationException("Missing connection string 'CheerfulGiver' in App.config.");

                var passphrase = ConfigurationManager.AppSettings["TokenPassphrase"]
                    ?? throw new InvalidOperationException("Missing appSetting TokenPassphrase in App.config.");

                var clientId = ConfigurationManager.AppSettings["BlackbaudClientId"]
                    ?? throw new InvalidOperationException("Missing appSetting BlackbaudClientId in App.config.");

                var clientSecret = ConfigurationManager.AppSettings["BlackbaudClientSecret"];
                if (string.IsNullOrWhiteSpace(clientSecret)) clientSecret = null;

                var subscriptionKey = ConfigurationManager.AppSettings["BlackbaudSubscriptionKey"]
                    ?? throw new InvalidOperationException("Missing appSetting BlackbaudSubscriptionKey in App.config.");

                // SQL-backed store + machine token provider
                SecretStore = new SqlBlackbaudSecretStore(sqlConnStr, passphrase);
                TokenProvider = new BlackbaudMachineTokenProvider(sqlConnStr, SecretStore, clientId, clientSecret);

                // STEP 1: Ensure subscription key exists in SQL (safe to call every startup)
                await SecretStore.SetGlobalSubscriptionKeyAsync(subscriptionKey);

                // STEP 3: One SKY API HttpClient for the whole app.
                var handler = new BlackbaudAuthHandler(TokenProvider)
                {
                    InnerHandler = new HttpClientHandler()
                };

                BlackbaudApiHttp = new HttpClient(handler)
                {
                    BaseAddress = new Uri("https://api.sky.blackbaud.com/")
                };
                BlackbaudApiHttp.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                ConstituentService = new RenxtConstituentLookupService(BlackbaudApiHttp);
                GiftService = new RenxtGiftServer(BlackbaudApiHttp);

                // Show main window (no StartupUri in App.xaml)
                var main = new MainWindow();
                main.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Startup error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(-1);
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try { BlackbaudApiHttp?.Dispose(); } catch { /* ignore */ }
            base.OnExit(e);
        }
    }
}
