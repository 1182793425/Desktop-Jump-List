using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.Views;

/// <summary>
/// Standalone settings surface: a centred dialog, independent from the launcher panel.
/// Closing it never re-opens the panel.
/// </summary>
public partial class SettingsWindow : Window
{
    private const string HotkeyHintDefault = "点击后按下新的组合键，Esc 取消";

    private bool _capturingHotkey;
    private bool _settingsRefreshing;

    public SettingsWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) => CenterOnScreen();
        Loaded += (_, _) =>
        {
            RefreshSettings();
            SettingsScroll.ScrollToTop();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
    }

    private void CenterOnScreen()
    {
        var area = ScreenGeometry.WorkAreaAtCursor(this);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingHotkey)
        {
            HandleHotkeyCapture(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_capturingHotkey) return;
        if (e.OriginalSource is DependencyObject source && IsInside(source, HotkeyBox)) return;
        CancelHotkeyCapture();
    }

    private void RefreshSettings()
    {
        var s = App.Current.Settings;
        SettingsNotice.Visibility = Visibility.Collapsed;

        ThemeSystem.IsChecked = s.Theme == AppTheme.System;
        ThemeLight.IsChecked = s.Theme == AppTheme.Light;
        ThemeDark.IsChecked = s.Theme == AppTheme.Dark;

        ColumnsValue.Text = s.Columns.ToString();
        RowsValue.Text = s.MaxRows.ToString();
        ColumnsMinus.IsEnabled = s.Columns > 3;
        ColumnsPlus.IsEnabled = s.Columns < 5;
        RowsMinus.IsEnabled = s.MaxRows > 2;
        RowsPlus.IsEnabled = s.MaxRows < 5;

        IconValue.Text = s.PanelIconSize.ToString();
        FontValue.Text = s.PanelFontSize.ToString();
        GapValue.Text = s.PanelGap.ToString();
        IconMinus.IsEnabled = s.PanelIconSize > 32;
        IconPlus.IsEnabled = s.PanelIconSize < 48;
        FontMinus.IsEnabled = s.PanelFontSize > 10;
        FontPlus.IsEnabled = s.PanelFontSize < 14;
        GapMinus.IsEnabled = s.PanelGap > 2;
        GapPlus.IsEnabled = s.PanelGap < 10;

        RecentValue.Text = s.RecentCount.ToString();
        RecentMinus.IsEnabled = s.RecentCount > 4;
        RecentPlus.IsEnabled = s.RecentCount < 20;

        SnapValue.Text = $"{s.SnapDistance} px";
        SnapMinus.IsEnabled = s.SnapDistance > 8;
        SnapPlus.IsEnabled = s.SnapDistance < 200;

        _settingsRefreshing = true;
        OpacitySlider.Value = Math.Round(s.PanelOpacity * 100);
        _settingsRefreshing = false;
        OpacityValue.Text = $"{Math.Round(s.PanelOpacity * 100)}%";

        DockVisibleSwitch.IsChecked = s.ShowDock;
        DockSizeValue.Text = $"{s.DockSize} px";
        DockSizeMinus.IsEnabled = s.DockSize > 32;
        DockSizePlus.IsEnabled = s.DockSize < 64;

        PeekValue.Text = $"{s.PeekDelayMs / 1000.0:0.0} 秒";
        PeekMinus.IsEnabled = s.PeekDelayMs > 600;
        PeekPlus.IsEnabled = s.PeekDelayMs < 8000;

        _settingsRefreshing = true;
        ColorWhite.IsChecked = string.Equals(s.DockColor, "#FFFFFF", StringComparison.OrdinalIgnoreCase);
        ColorGraphite.IsChecked = string.Equals(s.DockColor, "#2B2F36", StringComparison.OrdinalIgnoreCase);
        ColorGray.IsChecked = string.Equals(s.DockColor, "#D1D5DB", StringComparison.OrdinalIgnoreCase);
        ColorBlue.IsChecked = string.Equals(s.DockColor, "#3B82F6", StringComparison.OrdinalIgnoreCase);
        ColorGreen.IsChecked = string.Equals(s.DockColor, "#22C55E", StringComparison.OrdinalIgnoreCase);
        ColorOrange.IsChecked = string.Equals(s.DockColor, "#F59E0B", StringComparison.OrdinalIgnoreCase);
        ColorRose.IsChecked = string.Equals(s.DockColor, "#F43F5E", StringComparison.OrdinalIgnoreCase);
        _settingsRefreshing = false;

        HotkeyText.Text = s.Hotkey;
        HotkeySwitch.IsChecked = s.HotkeyEnabled;
        AutostartSwitch.IsChecked = StartupService.IsEnabled;
        if (!_capturingHotkey)
            HotkeyHint.Text = s.HotkeyEnabled
                ? HotkeyHintDefault
                : "默认关闭；打开开关后生效，点击这里可先改组合键";

        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Jump" : $"Jump v{version.ToString(3)}";
    }

    private void OnThemeSystem(object sender, RoutedEventArgs e) => ApplyThemeFromSettings(AppTheme.System);
    private void OnThemeLight(object sender, RoutedEventArgs e) => ApplyThemeFromSettings(AppTheme.Light);
    private void OnThemeDark(object sender, RoutedEventArgs e) => ApplyThemeFromSettings(AppTheme.Dark);

    private void ApplyThemeFromSettings(AppTheme mode)
    {
        App.Current.SetTheme(mode);
        RefreshSettings();
    }

    private void OnColumnsMinus(object sender, RoutedEventArgs e) => ChangeColumns(-1);
    private void OnColumnsPlus(object sender, RoutedEventArgs e) => ChangeColumns(1);
    private void OnRowsMinus(object sender, RoutedEventArgs e) => ChangeRows(-1);
    private void OnRowsPlus(object sender, RoutedEventArgs e) => ChangeRows(1);
    private void OnPeekMinus(object sender, RoutedEventArgs e) => ChangePeek(-400);
    private void OnPeekPlus(object sender, RoutedEventArgs e) => ChangePeek(400);

    private void OnIconMinus(object sender, RoutedEventArgs e) => ChangePanelIcon(-4);
    private void OnIconPlus(object sender, RoutedEventArgs e) => ChangePanelIcon(4);
    private void OnFontMinus(object sender, RoutedEventArgs e) => ChangePanelFont(-1);
    private void OnFontPlus(object sender, RoutedEventArgs e) => ChangePanelFont(1);
    private void OnGapMinus(object sender, RoutedEventArgs e) => ChangePanelGap(-2);
    private void OnGapPlus(object sender, RoutedEventArgs e) => ChangePanelGap(2);

    private void OnRecentMinus(object sender, RoutedEventArgs e) => ChangeRecentCount(-1);
    private void OnRecentPlus(object sender, RoutedEventArgs e) => ChangeRecentCount(1);

    private void OnSnapMinus(object sender, RoutedEventArgs e) => ChangeSnap(-8);
    private void OnSnapPlus(object sender, RoutedEventArgs e) => ChangeSnap(8);

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingsRefreshing || OpacityValue is null) return;
        App.Current.SetPanelOpacity(e.NewValue / 100.0);
        OpacityValue.Text = $"{Math.Round(e.NewValue)}%";
    }

    private void OnDockSizeMinus(object sender, RoutedEventArgs e) => ChangeDockSize(-4);
    private void OnDockSizePlus(object sender, RoutedEventArgs e) => ChangeDockSize(4);

    private void OnDockVisibleClick(object sender, RoutedEventArgs e)
    {
        App.Current.SetShowDock(DockVisibleSwitch.IsChecked == true);
        RefreshSettings();
    }

    private void OnDockColorChecked(object sender, RoutedEventArgs e)
    {
        if (_settingsRefreshing) return;
        if (sender is RadioButton { Tag: string color })
        {
            App.Current.SetDockColor(color);
            RefreshSettings();
        }
    }

    private void OnAutostartClick(object sender, RoutedEventArgs e)
    {
        var want = AutostartSwitch.IsChecked == true;
        var error = App.Current.SetAutostart(want);
        if (error is not null)
        {
            AutostartSwitch.IsChecked = !want;
        }
        RefreshSettings();
        if (error is not null)
        {
            SettingsNotice.Text = $"无法修改开机启动：{error}";
            SettingsNotice.Visibility = Visibility.Visible;
        }
    }

    private void ChangePanelIcon(int delta)
    {
        App.Current.SetPanelIconSize(App.Current.Settings.PanelIconSize + delta);
        RefreshSettings();
    }

    private void ChangePanelFont(int delta)
    {
        App.Current.SetPanelFontSize(App.Current.Settings.PanelFontSize + delta);
        RefreshSettings();
    }

    private void ChangePanelGap(int delta)
    {
        App.Current.SetPanelGap(App.Current.Settings.PanelGap + delta);
        RefreshSettings();
    }

    private void ChangeRecentCount(int delta)
    {
        App.Current.SetRecentCount(App.Current.Settings.RecentCount + delta);
        RefreshSettings();
    }

    private void ChangeSnap(int delta)
    {
        App.Current.SetSnapDistance(App.Current.Settings.SnapDistance + delta);
        RefreshSettings();
    }

    private void ChangeDockSize(int delta)
    {
        App.Current.SetDockSize(App.Current.Settings.DockSize + delta);
        RefreshSettings();
    }

    private void ChangeColumns(int delta)
    {
        App.Current.SetColumns(App.Current.Settings.Columns + delta);
        RefreshSettings();
    }

    private void ChangeRows(int delta)
    {
        App.Current.SetMaxRows(App.Current.Settings.MaxRows + delta);
        RefreshSettings();
    }

    private void ChangePeek(int delta)
    {
        App.Current.SetPeekDelay(App.Current.Settings.PeekDelayMs + delta);
        RefreshSettings();
    }

    private void OnHotkeyEnabledClick(object sender, RoutedEventArgs e)
    {
        var want = HotkeySwitch.IsChecked == true;
        var ok = App.Current.SetHotkeyEnabled(want, out var error);
        RefreshSettings();
        if (!ok)
        {
            HotkeySwitch.IsChecked = !want;
            HotkeyHint.Text = error ?? "无法启用该快捷键";
        }
    }

    private void OnHotkeyBoxClick(object sender, MouseButtonEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyText.Text = "按下组合键…";
        HotkeyHint.Text = "Esc 取消 · 至少需要一个修饰键";
        Keyboard.Focus(HotkeyBox);
        e.Handled = true;
    }

    private void HandleHotkeyCapture(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.Tab)
            return;

        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None)
        {
            HotkeyHint.Text = "至少需要 Ctrl / Alt / Shift / Win 中的一个";
            return;
        }

        var text = FormatHotkey(mods, key);
        if (App.Current.TrySetHotkey(text, out var error))
        {
            _capturingHotkey = false;
            RefreshSettings();
            HotkeyHint.Text = App.Current.Settings.HotkeyEnabled
                ? $"已更新为 {text}"
                : $"已设置为 {text}；打开开关后生效";
        }
        else
        {
            HotkeyHint.Text = error ?? "无法注册该快捷键";
        }
    }

    private void CancelHotkeyCapture()
    {
        if (!_capturingHotkey) return;
        _capturingHotkey = false;
        HotkeyHint.Text = App.Current.Settings.HotkeyEnabled
            ? HotkeyHintDefault
            : "默认关闭；打开左侧开关后生效，点击这里可先改组合键";
        HotkeyText.Text = App.Current.Settings.Hotkey;
    }

    private void OnSettingsDone(object sender, RoutedEventArgs e) => Close();
    private void OnOpenDataFolder(object sender, RoutedEventArgs e) => App.Current.OpenDataFolder();

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        for (var d = node; d is not null; d = VisualTreeHelper.GetParent(d))
            if (ReferenceEquals(d, ancestor)) return true;
        return false;
    }

    private static string FormatHotkey(ModifierKeys mods, Key key)
    {
        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key)
    {
        var text = new KeyConverter().ConvertToInvariantString(key);
        return string.IsNullOrWhiteSpace(text) ? key.ToString() : text;
    }
}
