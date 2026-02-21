using CheerfulGiverNXT.ViewModels;
using CheerfulGiverNXT.Workflow;
using System;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace CheerfulGiverNXT
{
    public partial class MainWindow : Window
    {
        private static readonly TimeSpan RefreshLeadTime = TimeSpan.FromMinutes(5);

        private readonly DispatcherTimer _tokenCountdownTimer;
        private string? _lastToken;
        private DateTimeOffset? _tokenExpiresAtUtc;

        public MainWindow()
        {
            InitializeComponent();

            var vm = new ConstituentLookupTestViewModel();
            DataContext = vm;
            vm.AddConstituentRequested += Vm_AddConstituentRequested;

            PreviewKeyDown += MainWindow_PreviewKeyDown;

            Loaded += async (_, __) =>
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SearchTextBox.Focus();
                    Keyboard.Focus(SearchTextBox);
                    SearchTextBox.SelectAll();
                }, DispatcherPriority.Input);

                await vm.RefreshAuthPreviewAsync();
            };

            _tokenCountdownTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _tokenCountdownTimer.Tick += (_, __) => UpdateTokenCountdown();
            _tokenCountdownTimer.Start();

            Closed += (_, __) => _tokenCountdownTimer.Stop();
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.S) return;
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;

            e.Handled = true;

            var win = new AdminSecretsWindow { Owner = this };
            win.ShowDialog();

            if (DataContext is ConstituentLookupTestViewModel vm)
                _ = vm.RefreshAuthPreviewAsync();
        }

        private void Vm_AddConstituentRequested(object? sender, ConstituentLookupTestViewModel.AddConstituentRequestedEventArgs e)
        {
            var result = MessageBox.Show(
                "No matches found after 3 searches.\n\nWould you like to create a new constituent record?",
                "Create New Constituent",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                RefocusSearch();
                return;
            }

            var win = new AddConstituentWindow(e.SearchText) { Owner = this };
            var ok = win.ShowDialog() == true;

            if (ok && DataContext is ConstituentLookupTestViewModel vm)
            {
                if (win.CreatedConstituentId is int id)
                    vm.StatusText = $"Created constituent {win.DraftDisplayName} (ID {id}).";
                else
                    vm.StatusText = $"Created constituent {win.DraftDisplayName}.";

                vm.SearchText = win.DraftDisplayName;
                if (vm.SearchCommand.CanExecute(null))
                    vm.SearchCommand.Execute(null);
            }

            RefocusSearch();
        }

        private void RefocusSearch()
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                SearchTextBox.Focus();
                Keyboard.Focus(SearchTextBox);
                SearchTextBox.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            if (DataContext is ConstituentLookupTestViewModel vm && vm.SearchCommand.CanExecute(null))
            {
                vm.SearchCommand.Execute(null);
                e.Handled = true;
            }
        }

        private async void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ConstituentLookupTestViewModel vm) return;
            if (vm.SelectedRow is null) return;

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;

                // Create the workflow context at selection time.
                var snapshot = new ConstituentSnapshot
                {
                    ConstituentId = vm.SelectedRow.Id,
                    FullName = vm.SelectedRow.FullName,
                    Spouse = vm.SelectedRow.Spouse,
                    Street = vm.SelectedRow.Street,
                    City = vm.SelectedRow.City,
                    State = vm.SelectedRow.State,
                    Zip = vm.SelectedRow.Zip
                };

                var workflow = GiftWorkflowContext.Start(vm.SearchText, snapshot);

                // existing placeholder call (kept)
                var funds = await vm.LookupService.GetContributedFundsAsync(vm.SelectedRow.Id, maxGiftsToScan: 500);
                _ = funds;

                var w = new GiftWindow(vm.SelectedRow, workflow) { Owner = this };
                w.ShowDialog();
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void UpdateTokenCountdown()
        {
            if (TokenCountdownTextBlock is null) return;

            if (DataContext is not ConstituentLookupTestViewModel vm)
            {
                TokenCountdownTextBlock.Text = "";
                return;
            }

            var token = vm.AccessToken;

            if (string.IsNullOrWhiteSpace(token))
            {
                _lastToken = null;
                _tokenExpiresAtUtc = null;
                TokenCountdownTextBlock.Text = "Token: (none)";
                return;
            }

            if (token.Length == 0)
            {
                // if your XAML still has this element
                if (FindName("ButtonAuthorize") is UIElement authBtn) authBtn.Visibility = Visibility.Visible;
            }
            else
            {
                if (FindName("ButtonAuthorize") is UIElement authBtn) authBtn.Visibility = Visibility.Collapsed;
            }

            if (!string.Equals(_lastToken, token, StringComparison.Ordinal))
            {
                _lastToken = token;
                _tokenExpiresAtUtc = TryGetJwtExpiryUtc(token);
            }

            if (_tokenExpiresAtUtc is null)
            {
                TokenCountdownTextBlock.Text = "Token: (unreadable)";
                return;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var refreshAtUtc = _tokenExpiresAtUtc.Value - RefreshLeadTime;

            var untilRefresh = refreshAtUtc - nowUtc;
            var untilExpiry = _tokenExpiresAtUtc.Value - nowUtc;

            if (untilExpiry <= TimeSpan.Zero)
            {
                TokenCountdownTextBlock.Text = "Token: expired";
                return;
            }

            if (untilRefresh <= TimeSpan.Zero)
            {
                TokenCountdownTextBlock.Text = "Token refresh: due";
                return;
            }

            TokenCountdownTextBlock.Text = $"Token refresh in {FormatHms(untilRefresh)}";
        }

        private static string FormatHms(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;

            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

            return $"{t.Minutes:00}:{t.Seconds:00}";
        }

        private static DateTimeOffset? TryGetJwtExpiryUtc(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;

                var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                using var doc = JsonDocument.Parse(payloadJson);

                if (!doc.RootElement.TryGetProperty("exp", out var expEl)) return null;

                long expSeconds = expEl.ValueKind switch
                {
                    JsonValueKind.Number => expEl.GetInt64(),
                    JsonValueKind.String => long.Parse(expEl.GetString() ?? "0"),
                    _ => 0
                };

                if (expSeconds <= 0) return null;
                return DateTimeOffset.FromUnixTimeSeconds(expSeconds);
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Base64UrlDecode(string input)
        {
            var s = input.Replace('-', '+').Replace('_', '/');

            switch (s.Length % 4)
            {
                case 0: break;
                case 2: s += "=="; break;
                case 3: s += "="; break;
                default: throw new FormatException("Invalid base64url string length.");
            }

            return Convert.FromBase64String(s);
        }
    }
}
