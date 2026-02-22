using CheerfulGiverNXT.ViewModels;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CheerfulGiverNXT;

public partial class GiftMatchAdminWindow : Window
{
    private GiftMatchAdminViewModel Vm => (GiftMatchAdminViewModel)DataContext;

    public GiftMatchAdminWindow()
    {
        InitializeComponent();
        DataContext = new GiftMatchAdminViewModel(App.GiftMatchService);

        Loaded += async (_, __) =>
        {
            WireButtons();
            await SafeRefreshAsync();
        };
    }

    private void WireButtons()
    {
        RefreshButton.Click += async (_, __) => await SafeRefreshAsync();
        SaveAnonButton.Click += async (_, __) => await RunBusyAsync(() => Vm.SaveAnonymousIdAsync());
        CreateChallengeButton.Click += async (_, __) => await RunBusyAsync(() => Vm.CreateChallengeAsync());
        DeactivateSelectedButton.Click += async (_, __) =>
        {
            if (Vm.SelectedChallenge is null) return;

            var msg = $"Deactivate '{Vm.SelectedChallenge.Name}'?\n\nThis stops it from matching new gifts. Existing matches remain.";
            if (MessageBox.Show(msg, "Deactivate challenge", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            await RunBusyAsync(() => Vm.DeactivateSelectedAsync());
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
            MessageBox.Show(ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }
}
