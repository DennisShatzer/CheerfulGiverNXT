using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace CheerfulGiverSQP.Tray;

/// <summary>
/// Minimal system tray integration for CheerfulGiverSQP.
/// = Close/minimize hides the window and keeps the processor running.
/// = Tray menu provides Open/Start/Stop/Exit.
///
/// No external packages are required; this uses WinForms NotifyIcon.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private readonly ViewModels.MainWindowViewModel _vm;

    public bool KeepRunningInTray { get; private set; } = true;

    public TrayIconService(Window window, ViewModels.MainWindowViewModel vm)
    {
        _window = window;
        _vm = vm;

        _vm.HideToTrayRequested += (_, _) => HideToTray();

        _icon = new NotifyIcon
        {
            Text = "CheerfulGiverSQP",
            Visible = true,
            Icon = TryLoadAppIcon() ?? SystemIcons.Application,
            ContextMenuStrip = BuildMenu()
        };

        _icon.DoubleClick += (_, _) => ShowFromTray();
    }

    private static Icon? TryLoadAppIcon()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(exe)) return null;
            return Icon.ExtractAssociatedIcon(exe!);
        }
        catch { return null; }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var open = new ToolStripMenuItem("Open dashboard");
        open.Click += (_, _) => ShowFromTray();

        var start = new ToolStripMenuItem("Start processing");
        start.Click += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => _vm.StartCommand.Execute(null));

        var stop = new ToolStripMenuItem("Stop processing");
        stop.Click += (_, _) => System.Windows.Application.Current.Dispatcher.Invoke(() => _vm.StopCommand.Execute(null));

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => Exit();

        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(start);
        menu.Items.Add(stop);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        return menu;
    }

    public void HideToTray()
    {
        if (!_vm.MinimizeToTray) return;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _window.Hide();
            _window.WindowState = WindowState.Normal;
        });

        _icon.ShowBalloonTip(1500, "CheerfulGiverSQP", "Running in the system tray.", ToolTipIcon.Info);
    }

    public void ShowFromTray()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (!_window.IsVisible)
                _window.Show();

            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;

            _window.Activate();
            _window.Topmost = true;  // quick flash to bring front
            _window.Topmost = false;
            _window.Focus();
        });
    }

    private void Exit()
    {
        KeepRunningInTray = false;
        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    public void Dispose()
    {
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { /* ignore */ }
    }
}

