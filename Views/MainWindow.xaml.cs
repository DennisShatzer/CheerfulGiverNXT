using CheerfulGiverNXT.ViewModels;
using CheerfulGiverNXT.Workflow;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CheerfulGiverNXT.Infrastructure.Logging;
using CheerfulGiverNXT.Infrastructure.Ui;
using CheerfulGiverNXT.Infrastructure.Theming;
using CheerfulGiverNXT.Infrastructure.AppMode;
using System.Windows.Media;

namespace CheerfulGiverNXT
{
    public partial class MainWindow : Window
    {
        // Prevent re-entrancy / loops when we set ThemeToggleButton.IsChecked in code.
        private bool _suppressThemeToggleEvents;

        private readonly string _baseTitle;


        // Tracks a constituent created via AddConstituentWindow so the next workflow can treat them as a new constituent.
        private int? _lastCreatedConstituentId;
        public MainWindow()
        {
            InitializeComponent();

            _baseTitle = Title;

            var vm = new ConstituentLookupViewModel();
            DataContext = vm;
            vm.AddConstituentRequested += Vm_AddConstituentRequested;

            PreviewKeyDown += MainWindow_PreviewKeyDown;

            Loaded += async (_, __) =>
            {
                // Ensure the UI toggle reflects the currently-applied theme.
                // This is UI-only state; it does NOT touch workflow, storage, or services.
                SyncThemeToggleFromThemeManager();

                ApplyModeUi();
                AppModeState.Instance.ModeChanged += (_, ____) => ApplyModeUi();

                await Dispatcher.InvokeAsync(() =>
                {
                    SearchTextBox.Focus();
                    Keyboard.Focus(SearchTextBox);
                    SearchTextBox.SelectAll();
                }, DispatcherPriority.Input);

                // Refresh the hidden auth preview (kept for diagnostics; UI is hidden)
                await vm.RefreshAuthPreviewAsync();
            };
        }

        private void ApplyModeUi()
        {
            try
            {
                var isDemo = AppModeState.Instance.IsDemo;
                var modeText = isDemo ? "Mode: Demo" : "Mode: Live";

                if (ModeIndicatorTextBlock != null)
                {
                    ModeIndicatorTextBlock.Text = modeText;

                    // Make DEMO more visually obvious.
                    var demoBrush = TryFindResource("CG.Brush.Accent") as Brush;
                    var normalBrush = TryFindResource("CG.Brush.TextMuted") as Brush;
                    ModeIndicatorTextBlock.Foreground = isDemo
                        ? (demoBrush ?? ModeIndicatorTextBlock.Foreground)
                        : (normalBrush ?? ModeIndicatorTextBlock.Foreground);
                }

                Title = isDemo ? (_baseTitle + " — DEMO") : _baseTitle;
            }
            catch
            {
                // UI-only
            }
        }

        // ------------------------------
        // Light/Dark Theme Toggle (UI-only)
        // ------------------------------
        // The theme palette is defined in Themes/Light.xaml and Themes/Dark.xaml.
        // This handler simply swaps ResourceDictionaries via ThemeManager.

        private void ThemeToggle_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressThemeToggleEvents) return;

            ThemeManager.Apply(AppTheme.Dark);
            UpdateThemeToggleLabel();
        }

        private void ThemeToggle_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_suppressThemeToggleEvents) return;

            ThemeManager.Apply(AppTheme.Light);
            UpdateThemeToggleLabel();
        }

        private void SyncThemeToggleFromThemeManager()
        {
            try
            {
                _suppressThemeToggleEvents = true;

                // Checked = Dark, Unchecked = Light
                if (ThemeToggleButton != null)
                    ThemeToggleButton.IsChecked = ThemeManager.Current == AppTheme.Dark;

                UpdateThemeToggleLabel();
            }
            finally
            {
                _suppressThemeToggleEvents = false;
            }
        }

        private void UpdateThemeToggleLabel()
        {
            if (ThemeToggleLabel == null) return;

            // Keep the label simple + non-intrusive.
            ThemeToggleLabel.Text = (ThemeManager.Current == AppTheme.Dark) ? "Dark" : "Light";
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+F12 opens Campaigns admin
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
                && (e.Key == Key.F12 || e.SystemKey == Key.F12))
            {
                e.Handled = true;
                var win = new CampaignsAdminWindow { Owner = this };
                win.ShowDialog();
                return;
            }

            // Ctrl+Shift hotkeys for admin tools (tolerant of extra modifiers)
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != (ModifierKeys.Control | ModifierKeys.Shift))
                return;

            if (e.Key == Key.S || e.SystemKey == Key.S)
            {
                // Ctrl+Shift+S opens Admin Secrets
                e.Handled = true;

                var win = new AdminSecretsWindow { Owner = this };
                win.ShowDialog();

                if (DataContext is ConstituentLookupViewModel vm)
                    _ = vm.RefreshAuthPreviewAsync();

                return;
            }

            if (e.Key == Key.D || e.SystemKey == Key.D)
            {
                // Ctrl+Shift+D toggles Demo mode
                e.Handled = true;
                AppModeState.Instance.Toggle();

                if (DataContext is ConstituentLookupViewModel vm)
                {
                    vm.StatusText = AppModeState.Instance.IsDemo
                        ? "DEMO mode enabled. Pledges will NOT be posted to SKY API."
                        : "LIVE mode enabled. Pledges will be posted to SKY API.";
                }

                ApplyModeUi();
                return;
            }

            if (e.Key == Key.C || e.SystemKey == Key.C)
            {
                // Ctrl+Shift+C opens Gift Match Challenge Admin
                e.Handled = true;
                var win = new GiftMatchAdminWindow { Owner = this };
                win.ShowDialog();
                return;
            }

            
            if (e.Key == Key.T || e.SystemKey == Key.T)
            {
                // Ctrl+Shift+T opens Local Transactions (audit)
                e.Handled = true;
                var win = new LocalTransactionsWindow { Owner = this };
                win.ShowDialog();
                return;
            }

            if (e.Key == Key.F || e.SystemKey == Key.F)
            {
                // Ctrl+Shift+F opens Campaigns admin (FundList is now the single source of truth).
                e.Handled = true;
                var win = new CampaignsAdminWindow { Owner = this };
                win.ShowDialog();
                return;
            }
        }

        private void Vm_AddConstituentRequested(object? sender, ConstituentLookupViewModel.AddConstituentRequestedEventArgs e)
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

            if (ok && DataContext is ConstituentLookupViewModel vm)
            {
                if (win.CreatedConstituentId is int id)
                {
                    vm.StatusText = $"Created constituent {win.DraftDisplayName} (Constituent ID: {id}).";
                    _lastCreatedConstituentId = id;
                }
                else
                {
                    vm.StatusText = $"Created constituent {win.DraftDisplayName}.";
                }

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

            if (DataContext is ConstituentLookupViewModel vm && vm.SearchCommand.CanExecute(null))
            {
                vm.SearchCommand.Execute(null);
                e.Handled = true;
            }
        }

        private void ResultsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is not ConstituentLookupViewModel vm) return;
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

                // Snapshot the current app mode into the workflow for audit + suppression.
                workflow.IsDemo = AppModeState.Instance.IsDemo;


                // If this row matches a constituent just created via the Add Constituent dialog, mark it as new for this workflow.
                workflow.IsNewConstituent = _lastCreatedConstituentId.HasValue && vm.SelectedRow.Id == _lastCreatedConstituentId.Value;
                if (workflow.IsNewConstituent)
                    _lastCreatedConstituentId = null;

                var w = new GiftWindow(vm.SelectedRow, workflow) { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                var context = $"ResultsGrid_MouseDoubleClick. SearchText='{vm.SearchText}'. SelectedId={vm.SelectedRow?.Id}";
                UiError.Show(ex, title: "Open Gift Window Error", context: context, message: "An error occurred opening the Gift window.", owner: this);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}