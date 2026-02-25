using System.ComponentModel;
using System.Windows;

namespace CheerfulGiverSQP.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        StateChanged += (_, _) =>
        {
            if (DataContext is ViewModels.MainWindowViewModel vm && vm.MinimizeToTray)
            {
                // The TrayIconService listens for HideToTray requests.
                if (WindowState == WindowState.Minimized)
                    vm.RequestHideToTray();
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Default behavior for this app is to keep running in the tray.
        // The tray menu provides an explicit Exit command.
        if (DataContext is ViewModels.MainWindowViewModel vm && vm.MinimizeToTray)
        {
            e.Cancel = true;
            vm.RequestHideToTray();
            return;
        }

        base.OnClosing(e);
    }
}
