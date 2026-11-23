using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace DesktopJumpList.Services;

/// <summary>Follows the Windows app theme (light/dark) and swaps brushes live.</summary>
public static class ThemeService
{
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; }

    public static void Initialize()
    {
        Apply(ReadSystemDark());
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            var dark = ReadSystemDark();
            if (dark != IsDark) Application.Current.Dispatcher.BeginInvoke(() => Apply(dark));
        };
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

    private static void Apply(bool dark)
    {
        IsDark = dark;
        var d = new ResourceDictionary();
        void Brush(string key, string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); d[key] = b; }
        void Col(string key, string hex) => d[key] = (Color)ColorConverter.ConvertFromString(hex);

        if (dark)
        {
            Brush("PanelBrush", "#F01E2128");
            Brush("PanelBorderBrush", "#22FFFFFF");
            Brush("PanelHighlightBrush", "#10FFFFFF");
            Brush("InkBrush", "#F1F3F7");
            Brush("InkSubBrush", "#9AA1AD");
            Brush("InkFaintBrush", "#5C6370");
            Brush("HoverBrush", "#14FFFFFF");
            Brush("PressBrush", "#1FFFFFFF");
            Brush("SelectedBrush", "#2E7EA2FF");
            Brush("SelectedBorderBrush", "#667EA2FF");
            Brush("AccentBrush", "#7EA2FF");
            Brush("PillBrush", "#F02A2E37");
            Brush("MenuBrush", "#FA262A32");
            Brush("MenuHoverBrush", "#1AFFFFFF");
            Brush("DropBrush", "#D91E2128");
            Brush("DashBrush", "#4D7EA2FF");
            Col("ShadowColor", "#000000");
            d["ShadowOpacity"] = 0.55;
        }
        else
        {
            Brush("PanelBrush", "#F5FBFBFD");
            Brush("PanelBorderBrush", "#14000000");
            Brush("PanelHighlightBrush", "#B3FFFFFF");
            Brush("InkBrush", "#1A1D24");
            Brush("InkSubBrush", "#6B7280");
            Brush("InkFaintBrush", "#A3A9B3");
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
