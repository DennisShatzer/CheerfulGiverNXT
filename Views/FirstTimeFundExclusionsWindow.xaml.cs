using CheerfulGiverNXT.Data;
using CheerfulGiverNXT.ViewModels;
using System;
using System.Configuration;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CheerfulGiverNXT.Infrastructure.Ui;

namespace CheerfulGiverNXT;

public partial class FirstTimeFundExclusionsWindow : Window
{
    private FirstTimeFundExclusionsViewModel Vm => (FirstTimeFundExclusionsViewModel)DataContext;

    public FirstTimeFundExclusionsWindow()
    {
        InitializeComponent();

        var cs = ConfigurationManager.ConnectionStrings["CheerfulGiver"]?.ConnectionString
                 ?? throw new InvalidOperationException("Missing connection string 'CheerfulGiver' in App.config.");

        var repo = new SqlFirstTimeFundExclusionsRepository(cs);

        DataContext = new FirstTimeFundExclusionsViewModel(repo, App.CampaignContext);

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
            UiError.Show(ex, title: "Error", context: "FirstTimeFundExclusionsWindow.xaml.RunBusyAsync", owner: this);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            IsEnabled = true;
        }
    }
}
