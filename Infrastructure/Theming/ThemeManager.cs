using System;
using System.Linq;
using System.Windows;

namespace CheerfulGiverNXT.Infrastructure.Theming;

/// <summary>
/// UI-only theme switcher.
/// Loads either Themes/Light.xaml or Themes/Dark.xaml into Application resources.
///
/// Notes:
/// - Uses DynamicResource lookups in XAML so switching updates at runtime.
/// - Does not touch workflow, storage, viewmodels, or services.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightUri = new("Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkUri = new("Themes/Dark.xaml", UriKind.Relative);

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app is null) return;

        // Remove previously loaded theme dictionaries
        for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var src = app.Resources.MergedDictionaries[i].Source?.OriginalString;
            if (src is not null && src.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }

        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = theme == AppTheme.Dark ? DarkUri : LightUri
        });

        Current = theme;
    }

    public static void Toggle() => Apply(Current == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
}
