using CheerfulGiverNXT.ViewModels;
using CheerfulGiverNXT.Workflow;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CheerfulGiverNXT.Infrastructure.Logging;

namespace CheerfulGiverNXT
{
    public partial class MainWindow : Window
    {
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

                // Refresh the hidden auth preview (kept for diagnostics; UI is hidden)
                await vm.RefreshAuthPreviewAsync();
            };
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+Shift hotkeys for admin tools
            if (Keyboard.Modifiers != (ModifierKeys.Control | ModifierKeys.Shift)) return;

            if (e.Key == Key.S)
            {
                // Ctrl+Shift+S opens Admin Secrets
                e.Handled = true;

                var win = new AdminSecretsWindow { Owner = this };
                win.ShowDialog();

                if (DataContext is ConstituentLookupTestViewModel vm)
                    _ = vm.RefreshAuthPreviewAsync();
            }
            else if (e.Key == Key.C)
            {
                // Ctrl+Shift+C opens Gift Match Challenge Admin
                e.Handled = true;
                var win = new GiftMatchAdminWindow { Owner = this };
                win.ShowDialog();
            }
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
                    vm.StatusText = $"Created constituent {win.DraftDisplayName} (Constituent ID: {id}).";
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

                // Existing placeholder call (kept)
                var funds = await vm.LookupService.GetContributedFundsAsync(vm.SelectedRow.Id, maxGiftsToScan: 500);
                _ = funds;

                var w = new GiftWindow(vm.SelectedRow, workflow) { Owner = this };
                w.ShowDialog();
            }
            catch (Exception ex)
            {
                var context = $"ResultsGrid_MouseDoubleClick. SearchText='{vm.SearchText}'. SelectedId={vm.SelectedRow?.Id}";
                var path = ErrorLogger.Log(ex, context);

                MessageBox.Show(
                    "An error occurred opening the Gift window and was logged to:\n\n" + path + "\n\n" +
                    "Please attach this file when reporting the issue.",
                    "Open Gift Window Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
    }
}