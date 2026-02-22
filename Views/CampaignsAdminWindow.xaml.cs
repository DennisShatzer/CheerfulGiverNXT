using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.ViewModels;
using System;
using System.Configuration;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace CheerfulGiverNXT;

public partial class CampaignsAdminWindow : Window
{
    private CampaignsAdminViewModel Vm => (CampaignsAdminViewModel)DataContext;

    public CampaignsAdminWindow()
    {
        InitializeComponent();

        var cs = ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString
                 ?? throw new InvalidOperationException("Missing connection string 'CheerfulGiver' in App.config.");

        var repo = new SqlCampaignsRepository(cs);
        DataContext = new CampaignsAdminViewModel(repo);

        Loaded += async (_, __) =>
        {
            WireButtons();
            await SafeRefreshAsync();
        };
    }

    private void WireButtons()
    {
        AddButton.Click += (_, __) => Vm.AddRow();
        RemoveButton.Click += (_, __) => Vm.RemoveSelected();
        ReloadButton.Click += async (_, __) => await SafeRefreshAsync();
        SaveButton.Click += async (_, __) => await RunBusyAsync(() => Vm.SaveAsync());
        CloseButton.Click += (_, __) => Close();
    }

    private async Task SafeRefreshAsync() =>
        await RunBusyAsync(() => Vm.RefreshAsync());

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
