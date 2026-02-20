using CheerfulGiverNXT.Auth;
using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Services;
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
    /// - Subscription key + tokens live in SQL (DPAPI-encrypted via SqlBlackbaudSecretStore).
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

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            try
            {
                var sqlConnStr = ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString
                    ?? throw new InvalidOperationException("Missing connection string 'CheerfulGiver' in App.config.");

                // ClientId is not secret; keep it in App.config for now.
                var clientId = ConfigurationManager.AppSettings["BlackbaudClientId"]
                    ?? throw new InvalidOperationException("Missing appSetting BlackbaudClientId in App.config.");

                // For PUBLIC PKCE apps, client secret is not used/stored.
                string? clientSecret = null;

                SecretStore = new SqlBlackbaudSecretStore(sqlConnStr);
                TokenProvider = new BlackbaudMachineTokenProvider(sqlConnStr, SecretStore, clientId, clientSecret);

                // Ensure subscription key exists in SQL (prompt if missing).
                await EnsureGlobalSubscriptionKeyAsync().ConfigureAwait(true);

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

        private static async Task EnsureGlobalSubscriptionKeyAsync()
        {
            var existing = await SecretStore.GetGlobalSubscriptionKeyAsync().ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(existing))
                return;

            var entered = PromptForSubscriptionKey();
            if (string.IsNullOrWhiteSpace(entered))
                throw new InvalidOperationException("No subscription key stored. Enter it to continue.");

            await SecretStore.SetGlobalSubscriptionKeyAsync(entered.Trim()).ConfigureAwait(true);
        }

        private static string? PromptForSubscriptionKey()
        {
            var win = new Window
            {
                Title = "Enter Blackbaud Subscription Key",
                Width = 520,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                Content = BuildSubscriptionKeyContent(out PasswordBox pb, out Button okBtn),
                Topmost = true
            };

            win.Loaded += (_, __) => pb.Focus();
            okBtn.Click += (_, __) => win.DialogResult = true;

            var result = win.ShowDialog();
            if (result == true)
                return pb.Password;

            return null;
        }

        private static UIElement BuildSubscriptionKeyContent(out PasswordBox pb, out Button okBtn)
        {
            var root = new Grid { Margin = new Thickness(14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var text = new TextBlock
            {
                Text = "This app needs your Blackbaud SKY API subscription key to call the API.\n" +
                       "It will be stored encrypted (DPAPI) in your local SQL Express database under __GLOBAL__.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(text, 0);
            root.Children.Add(text);

            pb = new PasswordBox
            {
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(pb, 1);
            root.Children.Add(pb);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            okBtn = new Button
            {
                Content = "OK",
                Width = 90,
                IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var cancel = new Button
            {
                Content = "Cancel",
                Width = 90,
                IsCancel = true
            };

            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancel);

            Grid.SetRow(btnPanel, 2);
            root.Children.Add(btnPanel);

            return root;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                BlackbaudApiHttp?.Dispose();
            }
            catch { /* ignore */ }

            base.OnExit(e);
        }
    }
}
