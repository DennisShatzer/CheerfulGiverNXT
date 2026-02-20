using CheerfulGiverNXT.ViewModels;
using System;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace CheerfulGiverNXT
{
    public partial class MainWindow : Window
    {
        // If your refresh logic kicks in earlier/later, adjust this to match.
        // Example: if you refresh exactly at expiry, set to TimeSpan.Zero.
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

            // Admin shortcut: Ctrl+Shift+S opens the Secrets/Admin screen.
            PreviewKeyDown += MainWindow_PreviewKeyDown;

            Loaded += async (_, __) =>
            {
                // Put the cursor in the search box when the window opens.
                await Dispatcher.InvokeAsync(() =>
                {
                    SearchTextBox.Focus();
                    Keyboard.Focus(SearchTextBox);
                    SearchTextBox.SelectAll();
                }, DispatcherPriority.Input);

                // Populate the read-only auth preview fields before the operator does anything.
                await vm.RefreshAuthPreviewAsync();
            };

            // Start a UI timer that shows "refresh in mm:ss" based on JWT exp.
            _tokenCountdownTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
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

            // Secrets may have been updated; refresh the preview fields.
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

                // Helpful default: populate the search box with the new name and run a search.
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

                var funds = await vm.LookupService.GetContributedFundsAsync(
                    vm.SelectedRow.Id,
                    maxGiftsToScan: 500);

                _ = funds; // placeholder until gift entry uses these

                var w = new GiftWindow(vm.SelectedRow /*, funds */)
                {
                    Owner = this
                };

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

            // If token changed (e.g., refresh occurred), recompute expiry from JWT.
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

            // Predict "when refresh should occur" based on expiry minus lead time.
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

            // Show H:MM:SS when >= 1 hour, else MM:SS
            if (t.TotalHours >= 1)
                return $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}";

            return $"{t.Minutes:00}:{t.Seconds:00}";
        }

        private static DateTimeOffset? TryGetJwtExpiryUtc(string jwt)
        {
            try
            {
                // JWT = header.payload.signature
                var parts = jwt.Split('.');
                if (parts.Length < 2) return null;

                var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
                using var doc = JsonDocument.Parse(payloadJson);
                if (!doc.RootElement.TryGetProperty("exp", out var expEl)) return null;

                // exp is seconds since Unix epoch
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
            // Base64url -> Base64
            var s = input.Replace('-', '+').Replace('_', '/');

            // Pad to multiple of 4
            switch (s.Length % 4)
            {
                case 0:
                    break;
                case 2:
                    s += "==";
                    break;
                case 3:
                    s += "=";
                    break;
                default:
                    throw new FormatException("Invalid base64url string length.");
            }

            return Convert.FromBase64String(s);
        }
    }
}
