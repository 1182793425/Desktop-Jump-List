using System.Windows;
using DesktopJumpList.Models;
using Microsoft.Win32;

namespace DesktopJumpList.Services;

public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static AppTheme _mode = AppTheme.System;

    public static event EventHandler? Changed;

    public static AppTheme Mode => _mode;
    public static bool IsDark { get; private set; }

    public static void Apply(AppTheme mode)
    {
        _mode = mode;
        var dark = mode == AppTheme.Dark || (mode == AppTheme.System && IsSystemDark());
        IsDark = dark;

        var app = Application.Current;
        if (app is null) return;

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/DesktopJumpList;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute)
        };

        var merged = app.Resources.MergedDictionaries;
        // App.xaml always keeps the shared styles at index 0 and the palette at index 1.
        if (merged.Count >= 2) merged[1] = dictionary;
        else merged.Add(dictionary);

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static AppTheme Next(AppTheme mode) => mode switch
    {
        AppTheme.System => AppTheme.Light,
        AppTheme.Light => AppTheme.Dark,
        _ => AppTheme.System
    };

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
