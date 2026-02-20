using System.Configuration;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CheerfulGiverNXT.Services;
using CheerfulGiverNXT.Auth;
using CheerfulGiverNXT.Infrastructure;
using CheerfulGiverNXT.Infrastructure.Configuration;

namespace CheerfulGiverNXT.ViewModels
{
    public sealed class ConstituentLookupTestViewModel : INotifyPropertyChanged
    {
        // UI bindings
        public AsyncRelayCommand LoginCommand { get; }
        public AsyncRelayCommand SearchCommand { get; }
        public RelayCommand CopySelectedIdCommand { get; }

        private CancellationTokenSource? _cts;

        private readonly BlackbaudMachineTokenProvider _tokenProvider;


        
        private const int NoMatchPromptThreshold = 3;
        private int _noMatchStreak;

        public event EventHandler<AddConstituentRequestedEventArgs>? AddConstituentRequested;

        public sealed class AddConstituentRequestedEventArgs : EventArgs
        {
            public string SearchText { get; }
            public AddConstituentRequestedEventArgs(string searchText) => SearchText = searchText;
        }

public ObservableCollection<RenxtConstituentLookupService.ConstituentGridRow> Results { get; } = new();

        public RenxtConstituentLookupService LookupService => App.ConstituentService;

        public ConstituentLookupTestViewModel()
        {
            _tokenProvider = App.TokenProvider;

            LoginCommand = new AsyncRelayCommand(AuthorizeThisComputerAsync, () => IsNotBusy);
            SearchCommand = new AsyncRelayCommand(SearchAsync, () => IsNotBusy);
            CopySelectedIdCommand = new RelayCommand(CopySelectedId, () => HasSelection);

            MachineName = Environment.MachineName;
            AccessToken = "(checking…)";
            SubscriptionKey = "(checking…)";
        }

        private string _machineName = "";
        public string MachineName
        {
            get => _machineName;
            private set { _machineName = value; OnPropertyChanged(); }
        }

        private string _accessToken = "";
        /// <summary>
        /// Debug/visibility only. Not used for API calls (auth headers are injected by handler).
        /// </summary>
        public string AccessToken
        {
            get => _accessToken;
            private set { _accessToken = value; OnPropertyChanged(); }
        }

        private string _subscriptionKey = "";
        /// <summary>
        /// Debug/visibility only. Not used for API calls (key header is injected by handler).
        /// </summary>
        public string SubscriptionKey
        {
            get => _subscriptionKey;
            private set { _subscriptionKey = value; OnPropertyChanged(); }
        }

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value; OnPropertyChanged(); }
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

        private bool _isAuthorized;
        public bool IsAuthorized
        {
            get => _isAuthorized;
            private set
            {
                if (_isAuthorized == value) return;
                _isAuthorized = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotAuthorized));
            }
        }

        public bool IsNotAuthorized => !IsAuthorized;

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

        

        /// <summary>
        /// Loads AccessToken/SubscriptionKey preview fields for the UI (read-only debug).
        /// Reads the subscription key directly from the GLOBAL SQL row (no auth required).
        /// </summary>
        public async Task RefreshAuthPreviewAsync(CancellationToken ct = default)
        {
            // Always try to read global subscription key (works even if this PC isn't authorized yet)
            try
            {
                var globalKey = await App.SecretStore.GetGlobalSubscriptionKeyAsync(ct);
                SubscriptionKey = globalKey is null ? "(not set in SQL)" : Preview(globalKey, 10);
            }
            catch
            {
                SubscriptionKey = "(error reading SQL)";
            }

            // Access token requires machine authorization
            try
            {
                var (token, _) = await _tokenProvider.GetAsync(ct);
                IsAuthorized = true;
                AccessToken = Preview(token, 18);
            }
            catch (InvalidOperationException)
            {
                IsAuthorized = false;
                AccessToken = "(not authorized – click Authorize this PC)";
            }
            catch
            {
                IsAuthorized = false;
                AccessToken = "(error)";
            }
        }

// STEP 2: Authorize this computer (stores refresh token in SQL under MACHINE:<COMPUTERNAME>)
        private async Task AuthorizeThisComputerAsync()
        {
            IsBusy = true;
            StatusText = "Opening browser to authorize this computer...";
            LoginCommand.RaiseCanExecuteChanged();
            SearchCommand.RaiseCanExecuteChanged();

            try
            {
                var redirectUri = BlackbaudConfig.RedirectUri;
                const string scope = "rnxt.r";

                await _tokenProvider.SeedThisMachineAsync(redirectUri, scope);

                // Load + show current values (for the debug expander)
                await LoadDebugAuthValuesAsync();

                StatusText = $"Authorized for this computer ({Environment.MachineName}).";
            }
            catch (Exception ex)
            {
                StatusText = "Authorization error: " + ex.Message;
            }
            finally
            {
                IsBusy = false;
                LoginCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
            }
        }

        private async Task SearchAsync()
        {
            var text = (SearchText ?? "").Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusText = "Type a name or street number + street (e.g., \"1068 Lindsay\").";
                return;
            }

            // If we need to prompt to add a new constituent, do it *after* the search completes
            // and the busy indicator has been cleared. Otherwise the progress bar stays active
            // while the operator is in the add flow.
            bool requestAddConstituent = false;
            string requestAddSearchText = text;

            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            IsBusy = true;
            SearchCommand.RaiseCanExecuteChanged();
            StatusText = "Searching...";

            try
            {
                Results.Clear();

                // Ensure we have a valid token & key (and update debug fields).
                // API calls themselves use the shared HttpClient (headers injected by handler).
                await LoadDebugAuthValuesAsync(_cts.Token);

                var rows = await App.ConstituentService.SearchGridAsync(text, _cts.Token);

                foreach (var row in rows)
                    Results.Add(row);

                StatusText = rows.Count == 0 ? "No matches." : $"Found {rows.Count} match(es).";

                if (rows.Count == 0)
                {
                    _noMatchStreak++;
                    if (_noMatchStreak >= NoMatchPromptThreshold)
                    {
                        _noMatchStreak = 0;
                        requestAddConstituent = true;
                        requestAddSearchText = text;
                    }
                }
                else
                {
                    _noMatchStreak = 0;
                }

            }
            catch (OperationCanceledException)
            {
                StatusText = "Search canceled.";
            }
            catch (InvalidOperationException ex)
            {
                StatusText = ex.Message;
                MessageBox.Show(ex.Message, "Not Authorized", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            if (requestAddConstituent)
            {
                AddConstituentRequested?.Invoke(this, new AddConstituentRequestedEventArgs(requestAddSearchText));
            }
        }

        private async Task LoadDebugAuthValuesAsync(CancellationToken ct = default)
        {
            // Subscription key: read from GLOBAL SQL row (works even if machine auth expires).
            try
            {
                var globalKey = await App.SecretStore.GetGlobalSubscriptionKeyAsync(ct);
                SubscriptionKey = globalKey is null ? "(not set in SQL)" : Preview(globalKey, 10);
            }
            catch
            {
                SubscriptionKey = "(error reading SQL)";
            }
            // Access token: requires machine authorization (refreshes automatically if needed).
            try
            {
                var (token, _) = await _tokenProvider.GetAsync(ct);
                IsAuthorized = true;

                // Don't show the full token on-screen; show a preview.
                AccessToken = Preview(token, 18);
            }
            catch (InvalidOperationException)
            {
                IsAuthorized = false;
                throw;
            }
        }

        private static string Preview(string s, int take)
        {
            if (string.IsNullOrWhiteSpace(s)) return "(empty)";
            s = s.Trim();
            return s.Length <= take ? s : s.Substring(0, take) + "…";
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
