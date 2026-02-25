using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CheerfulGiverNXT.Auth;
using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.Infrastructure.AppMode;
using CheerfulGiverNXT.Infrastructure;

namespace CheerfulGiverSQP.ViewModels;

public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private readonly SkyQueue.ProcessorHost _host;
    private readonly SkyQueue.SqlSkyTransactionRepository _repository;
    private readonly SqlBlackbaudSecretStore _secretStore;
    private readonly BlackbaudMachineTokenProvider _tokenProvider;

    // UI-thread marshal for all PropertyChanged + ObservableCollection updates.
    // ProcessorHost intentionally uses ConfigureAwait(false) in preflight/stop paths,
    // so events may arrive on a threadpool thread.
    private readonly Dispatcher _ui;

    private readonly DispatcherTimer _autoRefreshTimer;
    private bool _refreshInFlight;

    private readonly string _clientId;
    private readonly string _redirectUri;
    private readonly string _scope;

    private readonly SkyQueue.ProcessingOptions _options;

    public MainWindowViewModel(
        SkyQueue.ProcessorHost host,
        SkyQueue.SqlSkyTransactionRepository repository,
        SqlBlackbaudSecretStore secretStore,
        BlackbaudMachineTokenProvider tokenProvider,
        string clientId,
        string redirectUri,
        string scope,
        SkyQueue.ProcessingOptions options)
    {
        _host = host;
        _repository = repository;
        _secretStore = secretStore;
        _tokenProvider = tokenProvider;
        _clientId = clientId;
        _redirectUri = redirectUri;
        _scope = scope;
        _options = options;

        _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        RecentTransactions = new ObservableCollection<SkyQueue.SkyTransactionRow>();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        StopCommand = new AsyncRelayCommand(StopAsync, () => CanStop);
        OpenSecretsCommand = new RelayCommand(OpenSecrets);
        ExitCommand = new RelayCommand(() => System.Windows.Application.Current?.Shutdown());
        ClearLogCommand = new RelayCommand(() => { _log.Clear(); OnPropertyChanged(nameof(LogText)); });

        _host.StateChanged += (_, _) => Ui(() =>
        {
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
            OnPropertyChanged(nameof(StatusLine));
            RaiseCanExecChanged();
        });

        _host.LogLine += (_, line) => AppendLog(line);

        // Auto-refresh the grid so transaction status updates live while the worker runs.
        // This keeps the admin experience "watchable" without requiring manual refresh clicks.
        // NOTE: Use DispatcherTimer so all ObservableCollection updates happen on the UI thread.
        _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _ui)
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(_options.PollIntervalSeconds, 1, 5))
        };
        _autoRefreshTimer.Tick += (_, _) => _ = AutoRefreshTickAsync();
        _autoRefreshTimer.Start();

        // Initial load
        _ = RefreshAsync();
    }

    private void Ui(Action action)
    {
        if (_ui.CheckAccess())
        {
            action();
            return;
        }

        _ui.BeginInvoke(action);
    }

    // ------------------------------------------------------------
    // UI state
    // ------------------------------------------------------------

    // NOTE: CheerfulGiverSQP is built with UseWindowsForms=true (for NotifyIcon), which enables
    // implicit global usings for System.Drawing.*. That creates ambiguity for names like Brush/Color.
    // Always reference WPF media types explicitly in this ViewModel.
    private static readonly System.Windows.Media.Brush OkBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 125, 50));      // green
    private static readonly System.Windows.Media.Brush WarnBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 108, 0));     // orange
    private static readonly System.Windows.Media.Brush ErrorBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198, 40, 40));     // red
    private static readonly System.Windows.Media.Brush NeutralBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(97, 97, 97));      // gray

    public string ConfigBadgeText { get => _configBadgeText; private set { _configBadgeText = value; OnPropertyChanged(); } }
    private string _configBadgeText = "Config";

    public string ConfigBadgeToolTip { get => _configBadgeToolTip; private set { _configBadgeToolTip = value; OnPropertyChanged(); } }
    private string _configBadgeToolTip = "";

    public System.Windows.Media.Brush ConfigBadgeBrush { get => _configBadgeBrush; private set { _configBadgeBrush = value; OnPropertyChanged(); } }
    private System.Windows.Media.Brush _configBadgeBrush = NeutralBrush;

    public bool IsConfigurationValid { get => _isConfigurationValid; private set { _isConfigurationValid = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); RaiseCanExecChanged(); } }
    private bool _isConfigurationValid;

    public string AuthorizationBadgeText { get => _authorizationBadgeText; private set { _authorizationBadgeText = value; OnPropertyChanged(); } }
    private string _authorizationBadgeText = "Auth";

    public string AuthorizationBadgeToolTip { get => _authorizationBadgeToolTip; private set { _authorizationBadgeToolTip = value; OnPropertyChanged(); } }
    private string _authorizationBadgeToolTip = "";

    public System.Windows.Media.Brush AuthorizationBadgeBrush { get => _authorizationBadgeBrush; private set { _authorizationBadgeBrush = value; OnPropertyChanged(); } }
    private System.Windows.Media.Brush _authorizationBadgeBrush = NeutralBrush;

    public bool IsAuthorized { get => _isAuthorized; private set { _isAuthorized = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); RaiseCanExecChanged(); } }
    private bool _isAuthorized;

    public string PostingBadgeText { get => _postingBadgeText; private set { _postingBadgeText = value; OnPropertyChanged(); } }
    private string _postingBadgeText = "Posting";

    public string PostingBadgeToolTip { get => _postingBadgeToolTip; private set { _postingBadgeToolTip = value; OnPropertyChanged(); } }
    private string _postingBadgeToolTip = "";

    public System.Windows.Media.Brush PostingBadgeBrush { get => _postingBadgeBrush; private set { _postingBadgeBrush = value; OnPropertyChanged(); } }
    private System.Windows.Media.Brush _postingBadgeBrush = NeutralBrush;

    public bool IsPostingAllowed { get => _isPostingAllowed; private set { _isPostingAllowed = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanStart)); RaiseCanExecChanged(); } }
    private bool _isPostingAllowed;

    public bool StartMinimized { get; set; } = false;

    private bool _minimizeToTray = true;
    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set { _minimizeToTray = value; OnPropertyChanged(); }
    }

    public string StatusFilter
    {
        get => _statusFilter;
        set
        {
            if (_statusFilter == value) return;
            _statusFilter = value;
            OnPropertyChanged();
            _ = RefreshAsync();
        }
    }
    private string _statusFilter = "";

    public string StatusLine => _host.IsRunning
        ? $"Running (poll {Math.Max(1, _options.PollIntervalSeconds)}s, batch {_options.BatchSize})"
        : "Stopped";

    public bool CanStart => !_host.IsRunning && IsConfigurationValid && IsAuthorized && IsPostingAllowed;
    public bool CanStop => _host.IsRunning;

    public int PendingCount { get => _pendingCount; private set { _pendingCount = value; OnPropertyChanged(); } }
    public int ProcessingCount { get => _processingCount; private set { _processingCount = value; OnPropertyChanged(); } }
    public int SucceededCount { get => _succeededCount; private set { _succeededCount = value; OnPropertyChanged(); } }
    public int FailedCount { get => _failedCount; private set { _failedCount = value; OnPropertyChanged(); } }

    private int _pendingCount;
    private int _processingCount;
    private int _succeededCount;
    private int _failedCount;

    public ObservableCollection<SkyQueue.SkyTransactionRow> RecentTransactions { get; }

    public SkyQueue.SkyTransactionRow? SelectedTransaction
    {
        get => _selected;
        set
        {
            _selected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSummary));
            OnPropertyChanged(nameof(SelectedStatusNote));
            OnPropertyChanged(nameof(SelectedLastError));
            OnPropertyChanged(nameof(SelectedRequestJson));
        }
    }
    private SkyQueue.SkyTransactionRow? _selected;

    public string SelectedSummary => SelectedTransaction is null
        ? "Select a transaction to view details."
        : $"#{SelectedTransaction.SkyTransactionRecordId}  =  {SelectedTransaction.TransactionStatus}  =  {SelectedTransaction.TransactionType}";

    public string SelectedStatusNote => string.IsNullOrWhiteSpace(SelectedTransaction?.StatusNote)
        ? ""
        : "Note: " + SelectedTransaction!.StatusNote;

    public string SelectedLastError => string.IsNullOrWhiteSpace(SelectedTransaction?.LastProcessingErrorMessage)
        ? ""
        : "Error: " + SelectedTransaction!.LastProcessingErrorMessage;

    public string SelectedRequestJson => SelectedTransaction?.RequestJson ?? "";

    // ------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand OpenSecretsCommand { get; }
    public RelayCommand ExitCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    private void RaiseCanExecChanged()
    {
        StartCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    public event EventHandler? HideToTrayRequested;

    public void RequestHideToTray() => HideToTrayRequested?.Invoke(this, EventArgs.Empty);


    private async Task AutoRefreshTickAsync()
    {
        // Skip overlapping refresh calls.
        if (_refreshInFlight) return;
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshInFlight) return;
        _refreshInFlight = true;

        try
        {
            // Badges
            RefreshConfigBadge();
            await RefreshAuthorizationBadgeAsync();
            RefreshPostingBadge();

            var counts = await _repository.GetStatusCountsAsync();
            var recent = await _repository.GetRecentAsync(statusFilter: StatusFilter, top: 200);

            Ui(() =>
            {
                PendingCount = counts.Pending;
                ProcessingCount = counts.Processing;
                SucceededCount = counts.Succeeded;
                FailedCount = counts.Failed;

                var selectedId = SelectedTransaction?.SkyTransactionRecordId;

                RecentTransactions.Clear();
                foreach (var r in recent)
                    RecentTransactions.Add(r);

                // Preserve selection across refresh so the details panel doesn't "jump" while statuses update.
                if (selectedId.HasValue)
                    SelectedTransaction = RecentTransactions.FirstOrDefault(x => x.SkyTransactionRecordId == selectedId.Value);
            });
        }
        catch (Exception ex)
        {
            AppendLog("Refresh error: " + ex.Message);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private void RefreshConfigBadge()
    {
        // App.config is the single source of truth; this is a lightweight sanity check.
        var issues = new System.Collections.Generic.List<string>();
// Help admins confirm which runtime config file is actually being read.
// In .NET (Core/5+/6+/8+), AppDomainSetup doesn't expose ConfigurationFile.
// The most reliable way to discover the active config path is OpenExeConfiguration.
string cfgPath;
try
{
    cfgPath = System.Configuration.ConfigurationManager
        .OpenExeConfiguration(System.Configuration.ConfigurationUserLevel.None)
        .FilePath;
}
catch
{
    // Fallback = what ConfigurationManager expects by convention in the app base directory.
    cfgPath = System.IO.Path.Combine(
        AppContext.BaseDirectory,
        $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.dll.config");
}
        var cfgMeta = File.Exists(cfgPath)
            ? $"Config file: {cfgPath}\nLast write (UTC): {File.GetLastWriteTimeUtc(cfgPath):yyyy-MM-dd HH:mm:ss}Z"
            : $"Config file: {cfgPath}\n(Warning: file not found at runtime path)";

        if (string.IsNullOrWhiteSpace(_clientId) || _clientId.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
            issues.Add("BlackbaudClientId is missing or placeholder.");
        else if (!Guid.TryParse(_clientId, out _))
            issues.Add("BlackbaudClientId is not a GUID.");

        if (string.IsNullOrWhiteSpace(_redirectUri))
            issues.Add("BlackbaudRedirectUri is missing.");
        else if (!Uri.TryCreate(_redirectUri, UriKind.Absolute, out _))
            issues.Add("BlackbaudRedirectUri is not a valid absolute URI.");

        if (string.IsNullOrWhiteSpace(_scope))
            issues.Add("BlackbaudScopes is missing.");

        if (string.IsNullOrWhiteSpace(_tokenProvider.SubscriptionKey) || _tokenProvider.SubscriptionKey.Contains("REPLACE_WITH", StringComparison.OrdinalIgnoreCase))
            issues.Add("BlackbaudSubscriptionKey is missing or placeholder.");

        if (issues.Count == 0)
        {
            IsConfigurationValid = true;
            ConfigBadgeText = "Config OK";
            ConfigBadgeBrush = OkBrush;
            ConfigBadgeToolTip = "App.config required keys look valid." + Environment.NewLine + Environment.NewLine + cfgMeta;
            return;
        }

        IsConfigurationValid = false;
        ConfigBadgeText = "Config issue";
        ConfigBadgeBrush = ErrorBrush;
        ConfigBadgeToolTip = string.Join(Environment.NewLine, issues) + Environment.NewLine + Environment.NewLine + cfgMeta;
    }

    private void RefreshPostingBadge()
    {
        if (SkyPostingPolicy.IsPostingAllowed(out var reason))
        {
            IsPostingAllowed = true;
            PostingBadgeText = "Posting enabled";
            PostingBadgeBrush = OkBrush;
            PostingBadgeToolTip = "SKY API posting is allowed.";
            return;
        }

        IsPostingAllowed = false;
        PostingBadgeText = "Posting disabled";
        PostingBadgeBrush = NeutralBrush;
        PostingBadgeToolTip = string.IsNullOrWhiteSpace(reason) ? "SKY API posting is disabled." : reason;
    }

    private async Task RefreshAuthorizationBadgeAsync()
    {
        try
        {
            var state = await _tokenProvider.GetThisMachineAuthorizationStateAsync();

            IsAuthorized = state.IsAuthorized;

            if (state.IsAuthorized)
            {
                AuthorizationBadgeText = "Authorized";
                AuthorizationBadgeBrush = OkBrush;
                var exp = state.ExpiresAtUtc.HasValue ? state.ExpiresAtUtc.Value.ToString("yyyy-MM-dd HH:mm:ss") + "Z" : "(unknown)";
                var sc = string.IsNullOrWhiteSpace(state.Scope) ? "(unknown)" : state.Scope;
                AuthorizationBadgeToolTip = $"{state.MachineSecretKey}\nExpiresAtUtc: {exp}\nScope: {sc}";
                return;
            }

            AuthorizationBadgeText = "Not authorized";
            AuthorizationBadgeBrush = ErrorBrush;
            AuthorizationBadgeToolTip =
                $"{state.MachineSecretKey}\n{(string.IsNullOrWhiteSpace(state.Reason) ? "No tokens found." : state.Reason)}\n\nOpen Secrets and click 'Authorize this server'.";
        }
        catch (Exception ex)
        {
            IsAuthorized = false;
            AuthorizationBadgeText = "Auth error";
            AuthorizationBadgeBrush = WarnBrush;
            AuthorizationBadgeToolTip = ex.Message;
        }
    }

    private async Task StartAsync()
    {
        try
        {
            await _host.StartAsync();
            AppendLog("Started.");
                    await RefreshAsync();
}
        catch (Exception ex)
        {
            AppendLog("Start error: " + ex.Message);
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _host.StopAsync();
            AppendLog("Stopped.");
                    await RefreshAsync();
}
        catch (Exception ex)
        {
            AppendLog("Stop error: " + ex.Message);
        }
    }

    private void OpenSecrets()
    {
        try
        {
            var win = new Views.SecretsWindow
            {
                Owner = System.Windows.Application.Current.MainWindow,
                DataContext = new SecretsWindowViewModel(
                    secretStore: _secretStore,
                    tokenProvider: _tokenProvider,
                    clientId: _clientId,
                    redirectUri: _redirectUri,
                    scope: _scope)
            };

            win.ShowDialog();

            // After authorizing, immediately refresh the badges (authorization + posting + config).
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Secrets window error: " + ex.Message);
        }
    }

    // ------------------------------------------------------------
    // Log
    // ------------------------------------------------------------

    private readonly StringBuilder _log = new();

    public string LogText => _log.ToString();

    private void AppendLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        Ui(() =>
        {
            _log.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").AppendLine(line);
            OnPropertyChanged(nameof(LogText));
        });
    }

    // ------------------------------------------------------------
    // INotifyPropertyChanged
    // ------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler is null) return;

        if (_ui.CheckAccess())
        {
            handler(this, new PropertyChangedEventArgs(name));
            return;
        }

        _ui.BeginInvoke(new Action(() => handler(this, new PropertyChangedEventArgs(name))));
    }
}
