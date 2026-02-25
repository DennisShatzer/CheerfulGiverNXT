using CheerfulGiverNXT.ViewModels;
using Microsoft.Win32;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CheerfulGiverNXT.Infrastructure.Ui;

namespace CheerfulGiverNXT;

public partial class LocalTransactionsWindow : Window
{
    private LocalTransactionsViewModel Vm => (LocalTransactionsViewModel)DataContext;

    public LocalTransactionsWindow()
    {
        InitializeComponent();
        DataContext = new LocalTransactionsViewModel(App.GiftWorkflowStore, App.GiftService, App.GiftMatchService, App.SkyTransactionQueue);

        Loaded += async (_, __) =>
        {
            WireUi();
            await SafeRefreshAsync();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        };
    }

    private void WireUi()
    {
        RefreshButton.Click += async (_, __) => await SafeRefreshAsync();

        SearchBox.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            await SafeRefreshAsync();
        };

        CopyWorkflowIdButton.Click += (_, __) =>
        {
            if (Vm.SelectedTransaction is null) return;
            Clipboard.SetText(Vm.SelectedTransaction.WorkflowId.ToString());
            Vm.StatusText = "WorkflowId copied to clipboard.";
        };

        ExportCsvButton.Click += async (_, __) =>
        {
            var dlg = new SaveFileDialog
            {
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = ".csv",
                AddExtension = true,
                OverwritePrompt = true,
                FileName = $"LocalTransactions_{DateTime.Now:yyyyMMdd_HHmm}.csv"
            };

            if (dlg.ShowDialog(this) != true)
                return;

            await RunBusyAsync(async () =>
            {
                var count = await Vm.ExportCurrentTransactionsToCsvAsync(dlg.FileName);
                Vm.StatusText = $"Exported {count} row(s) to {Path.GetFileName(dlg.FileName)}.";
            });
        };

        DeleteButton.Click += async (_, __) =>
        {
            if (!Vm.CanDeleteSelected || Vm.SelectedTransaction is null)
                return;

            var t = Vm.SelectedTransaction;
            var hasSkyGift = !string.IsNullOrWhiteSpace(t.ApiGiftId);
            var isRetrySkyOnly = t.IsDeleted == true && hasSkyGift;

            var prompt =
                (isRetrySkyOnly
                    ? "This pledge is already marked as DELETED locally.\n\nThis will RE-ATTEMPT deleting the gift in Blackbaud SKY API.\n\n"
                    : "This will mark the selected pledge as DELETED in your local SQL database.\n\n")
                + (hasSkyGift
                    ? $"SKY Gift Id: {t.ApiGiftId}\nThis action will attempt to delete this gift in SKY API.\n\n"
                    : "No SKY Gift Id is present. Only the local record will be marked deleted.\n\n")
                + $"WorkflowId: {t.WorkflowId}\nConstituent: {t.ConstituentId} = {t.ConstituentName}\nAmount: {t.AmountText}\n\nContinue?";

            var result = MessageBox.Show(
                prompt,
                "Delete Pledge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            await RunBusyAsync(() => Vm.DeleteSelectedAsync());
        };

        RetrySubmitButton.Click += async (_, __) =>
        {
            if (!Vm.CanRetrySelected)
                return;

            // Best-effort duplicate check (does not block retry if the check fails).
            LocalTransactionsViewModel.SkyDuplicateCheckResult dup;
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                dup = await Vm.CheckSkyDuplicatesForSelectedAsync();
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            if (!string.IsNullOrWhiteSpace(dup.ErrorText) && !dup.HasDuplicates)
            {
                // Informational only.
                Vm.StatusText = "Duplicate check unavailable: " + dup.ErrorText;
            }

            var prompt = "This will retry submitting the stored pledge request to the Blackbaud SKY API, then update the local SQL record.\n\nContinue?";
            if (dup.HasDuplicates && !string.IsNullOrWhiteSpace(dup.WarningText))
            {
                prompt = dup.WarningText + "\n\n" + prompt;
            }

            var result = MessageBox.Show(
                prompt,
                "Retry Submit to SKY",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            await RunBusyAsync(() => Vm.RetrySelectedAsync());
        };
    }

    private async Task SafeRefreshAsync()
    {
        await RunBusyAsync(() => Vm.RefreshAsync());
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        try
        {
            IsEnabled = false;
            Mouse.OverrideCursor = Cursors.Wait;
            await action();
        }
        catch (Exception ex)
        {
            UiError.Show(ex, title: "Error", context: "LocalTransactionsWindow.xaml.RunBusyAsync", owner: this);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }
}
