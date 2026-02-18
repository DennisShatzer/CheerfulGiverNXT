using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CheerfulGiverNXT
{
    public sealed class ConstituentLookupTestViewModel : INotifyPropertyChanged
    {
        public string ClientId { get; set; } = "5e25d81f-82a3-4bcf-97ed-1cea54d5172e"; // from your Blackbaud app registration

        public AsyncRelayCommand LoginCommand { get; }

        public ConstituentLookupTestViewModel()
        {
            LoginCommand = new AsyncRelayCommand(LoginAsync, () => IsNotBusy);
            SearchCommand = new AsyncRelayCommand(SearchAsync, () => IsNotBusy);
            CopySelectedIdCommand = new RelayCommand(CopySelectedId, () => HasSelection);
        }

        private async Task LoginAsync()
        {
            IsBusy = true;
            try
            {
                // Register this exact redirect URI in your Blackbaud app
                const string redirectUri = "http://127.0.0.1:5001/auth/callback/";
                const string scope = "rnxt.r";

                var token = await BlackbaudPkceAuth.AcquireTokenAsync(ClientId, redirectUri, scope);

                AccessToken = token.AccessToken;
                StatusText = $"Logged in. Expires in ~{token.ExpiresIn}s.";
            }
            catch (Exception ex)
            {
                StatusText = "Login error: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
                LoginCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
            }
        }


        private readonly HttpClient _httpClient = new HttpClient();

        public ObservableCollection<RenxtConstituentLookupService.ConstituentGridRow> Results { get; } = new();

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(); }
        }

        private string _accessToken = Environment.GetEnvironmentVariable("BB_ACCESS_TOKEN") ?? "";
        public string AccessToken
        {
            get => _accessToken;
            set { _accessToken = value; OnPropertyChanged(); }
        }

        private string _subscriptionKey = Environment.GetEnvironmentVariable("BB_SUBSCRIPTION_KEY") ?? "";
        public string SubscriptionKey
        {
            get => _subscriptionKey;
            set { _subscriptionKey = value; OnPropertyChanged(); }
        }

        private string _statusText = "Ready.";
        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotBusy));
                OnPropertyChanged(nameof(BusyVisibility));
            }
        }

        public bool IsNotBusy => !IsBusy;

        public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

        private RenxtConstituentLookupService.ConstituentGridRow? _selectedRow;
        public RenxtConstituentLookupService.ConstituentGridRow? SelectedRow
        {
            get => _selectedRow;
            set
            {
                _selectedRow = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                CopySelectedIdCommand.RaiseCanExecuteChanged();
            }
        }

        public bool HasSelection => SelectedRow is not null;

        public AsyncRelayCommand SearchCommand { get; }
        public RelayCommand CopySelectedIdCommand { get; }

        private CancellationTokenSource? _cts;

        private async Task SearchAsync()
        {
            var text = (SearchText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = "Type a name or street number + street (e.g., \"1068 Lindsay\").";
                return;
            }

            if (string.IsNullOrWhiteSpace(AccessToken) || string.IsNullOrWhiteSpace(SubscriptionKey))
            {
                StatusText = "Missing Access Token or Subscription Key.";
                return;
            }

            // cancel prior search
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            IsBusy = true;
            SearchCommand.RaiseCanExecuteChanged();
            StatusText = "Searching...";

            try
            {
                Results.Clear();

                // Ensure headers can be updated between runs by clearing prior values.
                _httpClient.DefaultRequestHeaders.Authorization = null;
                if (_httpClient.DefaultRequestHeaders.Contains("Bb-Api-Subscription-Key"))
                    _httpClient.DefaultRequestHeaders.Remove("Bb-Api-Subscription-Key");

                // Create service (it will set BaseAddress + headers)
                var svc = new RenxtConstituentLookupService(_httpClient, AccessToken, SubscriptionKey);

                var rows = await svc.SearchGridAsync(text, _cts.Token);

                foreach (var row in rows)
                    Results.Add(row);

                StatusText = rows.Count == 0 ? "No matches." : $"Found {rows.Count} match(es).";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Search canceled.";
            }
            catch (Exception ex)
            {
                StatusText = "Error: " + ex.Message;
                MessageBox.Show("Error during search:\n" + ex, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
                SearchCommand.RaiseCanExecuteChanged();
            }
        }

        private void CopySelectedId()
        {
            if (SelectedRow is null) return;
            Clipboard.SetText(SelectedRow.Id.ToString());
            StatusText = $"Copied Constituent ID {SelectedRow.Id} to clipboard.";
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
