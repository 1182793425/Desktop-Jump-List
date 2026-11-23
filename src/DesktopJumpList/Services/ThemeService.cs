using System.Windows;
using System.Windows.Media;
using DesktopJumpList.Models;
using Microsoft.Win32;

namespace DesktopJumpList.Services;

/// <summary>
/// Applies the colour palette (system-following or forced light/dark) by swapping a
/// ResourceDictionary full of DynamicResource brushes. Because every surface binds via
/// DynamicResource, changing the mode repaints the whole app instantly.
/// </summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;
    private static AppTheme _mode = AppTheme.System;

    public static bool IsDark { get; private set; }

    public static void Initialize(AppTheme mode)
    {
        _mode = mode;
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            if (_mode == AppTheme.System)
                Application.Current.Dispatcher.BeginInvoke(Apply);
        };
    }

    public static void SetMode(AppTheme mode)
    {
        if (_mode == mode) return;
        _mode = mode;
        Apply();
    }

    private static bool ReadSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    private static void Apply()
    {
        var dark = _mode switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => ReadSystemDark()
        };
        if (dark == IsDark && _current is not null) return;
        IsDark = dark;

        var d = new ResourceDictionary();
        void Brush(string key, string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); d[key] = b; }
        void Col(string key, string hex) => d[key] = (Color)ColorConverter.ConvertFromString(hex);

        if (dark)
        {
            // Opaque-ish surface so light text never washes out against a bright wallpaper.
            Brush("PanelBrush", "#F51B1F27");
            Brush("PanelBorderBrush", "#2EFFFFFF");
            Brush("PanelHighlightBrush", "#0DFFFFFF");
            Brush("InkBrush", "#F2F4F8");        // primary text — high contrast on dark
            Brush("InkSubBrush", "#B4BBC7");     // secondary text — clearly readable
            Brush("InkFaintBrush", "#828A99");   // captions/hints — dimmed but legible
            Brush("HoverBrush", "#14FFFFFF");
            Brush("PressBrush", "#1FFFFFFF");
            Brush("SelectedBrush", "#338AB0FF");
            Brush("SelectedBorderBrush", "#738AB0FF");
            Brush("AccentBrush", "#8AB0FF");     // brighter accent for dark surfaces
            Brush("PillBrush", "#F4282C36");
            Brush("MenuBrush", "#FC23262E");
            Brush("MenuHoverBrush", "#1AFFFFFF");
            Brush("DropBrush", "#E61B1F27");
            Brush("DashBrush", "#668AB0FF");
            Col("ShadowColor", "#000000");
            d["ShadowOpacity"] = 0.55;
        }
        else
        {
            Brush("PanelBrush", "#F8FCFCFE");
            Brush("PanelBorderBrush", "#16000000");
            Brush("PanelHighlightBrush", "#B3FFFFFF");
            Brush("InkBrush", "#1A1D24");
            Brush("InkSubBrush", "#5C6470");
            Brush("InkFaintBrush", "#8F97A3");
            Brush("HoverBrush", "#0B000000");
            Brush("PressBrush", "#14000000");
            Brush("SelectedBrush", "#1F3B6CF6");
            Brush("SelectedBorderBrush", "#593B6CF6");
            Brush("AccentBrush", "#3B6CF6");
            Brush("PillBrush", "#F5FFFFFF");
            Brush("MenuBrush", "#FCFFFFFF");
            Brush("MenuHoverBrush", "#0F000000");
            Brush("DropBrush", "#E6F6F8FC");
            Brush("DashBrush", "#663B6CF6");
            Col("ShadowColor", "#1E2A4A");
            d["ShadowOpacity"] = 0.28;
        }

        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, d);
        _current = d;
    }
}
