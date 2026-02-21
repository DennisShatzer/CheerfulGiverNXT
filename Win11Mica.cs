using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CheerfulGiverNXT;

/// <summary>
/// Enables Windows 11 system backdrop (Mica) + rounded corners for WPF windows.
/// Safe no-op on Windows 10 or unsupported builds.
/// </summary>
public static class Win11Mica
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(Win11Mica),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window)
            return;

        if (e.NewValue is true)
        {
            // Apply when handle is available
            window.SourceInitialized -= Window_SourceInitialized;
            window.SourceInitialized += Window_SourceInitialized;

            // If already initialized, try immediately
            if (PresentationSource.FromVisual(window) is not null)
            {
                TryApply(window);
            }
        }
        else
        {
            window.SourceInitialized -= Window_SourceInitialized;
        }
    }

    private static void Window_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
            TryApply(window);
    }

    private static void TryApply(Window window)
    {
        // Windows 11 is 10.0.22000+
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        // Rounded corners (Win11)
        int cornerPref = (int)DwmWindowCornerPreference.DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, (int)DwmWindowAttribute.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

        // System backdrop (Mica)
        int backdrop = (int)DwmSystemBackdropType.DWMSBT_MAINWINDOW;
        int hr = DwmSetWindowAttribute(hwnd, (int)DwmWindowAttribute.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        // Fallback for early Win11 builds where SYSTEMBACKDROP may be present but not effective:
        // (Undocumented/unsupported, but widely used in 22000-era samples.)
        if (hr != 0)
        {
            int enable = 1;
            _ = DwmSetWindowAttribute(hwnd, (int)DwmWindowAttribute.DWMWA_MICA_EFFECT, ref enable, sizeof(int));
        }
    }

    // ====== DWM interop ======

    // Documented system backdrop type enum
    // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type
    private enum DwmSystemBackdropType
    {
        DWMSBT_AUTO = 0,
        DWMSBT_NONE = 1,
        DWMSBT_MAINWINDOW = 2,
        DWMSBT_TRANSIENTWINDOW = 3,
        DWMSBT_TABBEDWINDOW = 4
    }

    // Rounded corner preference enum values
    // DWMWCP_DEFAULT=0, DONOTROUND=1, ROUND=2, ROUNDSMALL=3
    private enum DwmWindowCornerPreference
    {
        DWMWCP_DEFAULT = 0,
        DWMWCP_DONOTROUND = 1,
        DWMWCP_ROUND = 2,
        DWMWCP_ROUNDSMALL = 3
    }

    // DWM window attribute ids (some are documented, some are legacy)
    private enum DwmWindowAttribute
    {
        // Win11 documented
        DWMWA_WINDOW_CORNER_PREFERENCE = 33,
        DWMWA_SYSTEMBACKDROP_TYPE = 38,

        // Legacy/undocumented (Win11 22000 era)
        DWMWA_MICA_EFFECT = 1029
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
