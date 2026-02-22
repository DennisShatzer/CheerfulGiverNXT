using CheerfulGiverNXT.ViewModels;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CheerfulGiverNXT
{
    public partial class AdminSecretsWindow : Window
    {
        private AdminSecretsViewModel Vm => (AdminSecretsViewModel)DataContext;

        public AdminSecretsWindow()
        {
            InitializeComponent();
            DataContext = new AdminSecretsViewModel();

            Loaded += async (_, __) =>
            {
                WireShowHideHandlers();
                WireButtons();
                await SafeRefreshAsync();
                UpdateSaveButtonsState();
            };
        }

        private void WireButtons()
        {
            SaveSubscriptionKeyButton.Click += async (_, __) => await SaveSubscriptionKeyAsync();
            ClearSubscriptionKeyButton.Click += async (_, __) => await ClearSecretAsync("subscription");

            SaveClientSecretButton.Click += async (_, __) => await SaveClientSecretAsync();
            ClearClientSecretButton.Click += async (_, __) => await ClearSecretAsync("client_secret");

            ClearMachineTokensButton.Click += async (_, __) => await ClearMachineTokensAsync();
            AuthorizeNowButton.Click += async (_, __) => await AuthorizeThisPcAsync();
        }

        private void WireShowHideHandlers()
        {
            ShowSubscriptionKeyCheckBox.Checked += (_, __) => { ToggleSecretVisibility(SubscriptionKeyPasswordBox, SubscriptionKeyTextBox, true); UpdateSaveButtonsState(); };
            ShowSubscriptionKeyCheckBox.Unchecked += (_, __) => { ToggleSecretVisibility(SubscriptionKeyPasswordBox, SubscriptionKeyTextBox, false); UpdateSaveButtonsState(); };
            SubscriptionKeyPasswordBox.PasswordChanged += (_, __) =>
            {
                if (ShowSubscriptionKeyCheckBox.IsChecked == true)
                    SubscriptionKeyTextBox.Text = SubscriptionKeyPasswordBox.Password;
                UpdateSaveButtonsState();
            };
            SubscriptionKeyTextBox.TextChanged += (_, __) =>
            {
                if (ShowSubscriptionKeyCheckBox.IsChecked == true)
                    SubscriptionKeyPasswordBox.Password = SubscriptionKeyTextBox.Text;
                UpdateSaveButtonsState();
            };

            ShowClientSecretCheckBox.Checked += (_, __) => { ToggleSecretVisibility(ClientSecretPasswordBox, ClientSecretTextBox, true); UpdateSaveButtonsState(); };
            ShowClientSecretCheckBox.Unchecked += (_, __) => { ToggleSecretVisibility(ClientSecretPasswordBox, ClientSecretTextBox, false); UpdateSaveButtonsState(); };
            ClientSecretPasswordBox.PasswordChanged += (_, __) =>
            {
                if (ShowClientSecretCheckBox.IsChecked == true)
                    ClientSecretTextBox.Text = ClientSecretPasswordBox.Password;
                UpdateSaveButtonsState();
            };
            ClientSecretTextBox.TextChanged += (_, __) =>
            {
                if (ShowClientSecretCheckBox.IsChecked == true)
                    ClientSecretPasswordBox.Password = ClientSecretTextBox.Text;
                UpdateSaveButtonsState();
            };
        }

        private static void ToggleSecretVisibility(PasswordBox pb, TextBox tb, bool show)
        {
            if (show)
            {
                tb.Text = pb.Password;
                tb.Visibility = Visibility.Visible;
                pb.Visibility = Visibility.Collapsed;
                tb.Focus();
                tb.SelectAll();
            }
            else
            {
                pb.Password = tb.Text;
                pb.Visibility = Visibility.Visible;
                tb.Visibility = Visibility.Collapsed;
                pb.Focus();
                pb.SelectAll();
            }
        }

        private string GetEnteredSubscriptionKey() =>
            ShowSubscriptionKeyCheckBox.IsChecked == true ? SubscriptionKeyTextBox.Text : SubscriptionKeyPasswordBox.Password;

        private string GetEnteredClientSecret() =>
            ShowClientSecretCheckBox.IsChecked == true ? ClientSecretTextBox.Text : ClientSecretPasswordBox.Password;

        
        private void UpdateSaveButtonsState()
        {
            // Disable Save buttons unless the admin has typed a value into the corresponding field.
            SaveSubscriptionKeyButton.IsEnabled = !string.IsNullOrWhiteSpace((GetEnteredSubscriptionKey() ?? "").Trim());
            SaveClientSecretButton.IsEnabled = !string.IsNullOrWhiteSpace((GetEnteredClientSecret() ?? "").Trim());
        }

private async Task SaveSubscriptionKeyAsync()
        {
            var value = (GetEnteredSubscriptionKey() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                MessageBox.Show("Please enter the subscription key.", "Missing value", MessageBoxButton.OK, MessageBoxImage.Warning);
                SubscriptionKeyPasswordBox.Focus();
                return;
            }

            await RunBusyAsync(async () =>
            {
                await Vm.SaveSubscriptionKeyAsync(value);
                ClearSubscriptionInputs();
            });

            MessageBox.Show("Subscription key saved to SQL (DPAPI LocalMachine).", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task SaveClientSecretAsync()
        {
            var value = (GetEnteredClientSecret() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                MessageBox.Show("Please enter the OAuth client secret.", "Missing value", MessageBoxButton.OK, MessageBoxImage.Warning);
                ClientSecretPasswordBox.Focus();
                return;
            }

            await RunBusyAsync(async () =>
            {
                await Vm.SaveClientSecretAsync(value);
                ClearClientSecretInputs();
            });

            MessageBox.Show(
                "Client secret saved to SQL (DPAPI LocalMachine).\n\nRestart the app for the new client secret to be used for token refresh.",
                "Saved",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private async Task ClearSecretAsync(string which)
        {
            var prompt = which == "subscription"
                ? "This will remove the stored subscription key from SQL. The app will prompt again on next start.\n\nContinue?"
                : "This will remove the stored OAuth client secret from SQL. Token refresh will break until it is set again.\n\nContinue?";

            var title = which == "subscription" ? "Clear Subscription Key" : "Clear OAuth Client Secret";
            if (MessageBox.Show(prompt, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await RunBusyAsync(async () =>
            {
                if (which == "subscription")
                    await Vm.ClearSubscriptionKeyAsync();
                else
                    await Vm.ClearClientSecretAsync();

                ClearSubscriptionInputs();
                ClearClientSecretInputs();
            });
        }

        private async Task ClearMachineTokensAsync()
        {
            var prompt = "This will remove this PC’s stored tokens from SQL.\n\nAfter clearing, you must re-authorize this PC before searching.\n\nContinue?";
            if (MessageBox.Show(prompt, "Clear This PC Tokens", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await RunBusyAsync(async () => await Vm.ClearMachineTokensAsync());
            MessageBox.Show("This PC tokens cleared.", "Cleared", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async Task AuthorizeThisPcAsync()
        {
            await RunBusyAsync(async () =>
            {
                await Vm.AuthorizeThisPcAsync();
                await Vm.RefreshAsync();
            });
        }

        private async Task SafeRefreshAsync()
        {
            try
            {
                await Vm.RefreshAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Unable to load status:\n\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearSubscriptionInputs()
        {
            SubscriptionKeyPasswordBox.Clear();
            SubscriptionKeyTextBox.Text = "";
            UpdateSaveButtonsState();
        }

        private void ClearClientSecretInputs()
        {
            ClientSecretPasswordBox.Clear();
            ClientSecretTextBox.Text = "";
            UpdateSaveButtonsState();
        }

        private async Task RunBusyAsync(Func<Task> action)
        {
            try
            {
                IsEnabled = false;
                Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                await action();
            }
            finally
            {
                Mouse.OverrideCursor = null;
                IsEnabled = true;
                await SafeRefreshAsync();
                UpdateSaveButtonsState();
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await SafeRefreshAsync();
                UpdateSaveButtonsState();
        }
    }
}
