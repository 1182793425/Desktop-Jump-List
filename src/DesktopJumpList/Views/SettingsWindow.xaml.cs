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
    private const string HotkeyHintDefault = "点击可改组合键";

    private bool _capturingHotkey;
    private bool _settingsRefreshing;

    public SettingsWindow()
    {
        InitializeComponent();
        NavigateTo("appearance");

        SourceInitialized += (_, _) => CenterOnScreen();
        Loaded += (_, _) =>
        {
            RefreshSettings();
            SettingsScroll.ScrollToTop();
            GrowToFitContent();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        MouseLeftButtonDown += OnWindowDragDown;
    }

    /// <summary>
    /// 无边框窗口整页可拖动：按下空白处（标题、卡片背景、说明文字等）即拖动窗口。
    /// 交互控件（按钮、输入框、滑块、开关、列表等）不拦截，热键捕获框也放行。
    /// </summary>
    private void OnWindowDragDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && HitsInteractive(source)) return;
        try { DragMove(); } catch { /* released mid-call: nothing to do */ }
    }

    private static bool HitsInteractive(DependencyObject? node)
    {
        while (node is not null)
        {
            // Control covers buttons, text boxes, sliders, switches, radio dots, list boxes...
            if (node is System.Windows.Controls.Control) return true;
            if (node is FrameworkElement { Name: nameof(HotkeyBox) }) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void CenterOnScreen()
    {
        var area = ScreenGeometry.WorkAreaAtCursor(this);

        // The card stack is compact, but a short work area (small laptop, heavy scaling) must not
        // cut the window off: shrink it and let the scroll viewer take over.
        var maxHeight = Math.Max(420, area.Height - 24);
        var maxWidth = Math.Max(480, area.Width - 24);
        if (Height > maxHeight) Height = maxHeight;
        if (Width > maxWidth) Width = maxWidth;

        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    /// <summary>
    /// The card stack grows as settings are added; only grow when the content really needs it,
    /// so a short window never shows a scrollbar for two pixels of slack.
    /// </summary>
    private void GrowToFitContent()
    {
        UpdateLayout();
        var area = ScreenGeometry.WorkAreaAtCursor(this);
        var chrome = ActualHeight - SettingsScroll.ActualHeight;
        var wanted = Math.Ceiling(chrome + SettingsScroll.ExtentHeight + 4);
        var max = Math.Max(420, area.Height - 24);
        if (wanted <= Height + 1 || wanted > max) return;
        Height = wanted;
        CenterOnScreen();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
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

        ColumnsBox.Text = s.Columns.ToString();
        RowsBox.Text = s.MaxRows.ToString();

        IconBox.Text = s.PanelIconSize.ToString();
        FontBox.Text = s.PanelFontSize.ToString();
        GapBox.Text = s.PanelGap.ToString();
        ItemGapBox.Text = s.PanelItemGap.ToString();

        RecentBox.Text = s.RecentCount.ToString();

        SnapBox.Text = s.SnapDistance.ToString();

        _settingsRefreshing = true;
        OpacitySlider.Value = Math.Round(s.PanelOpacity * 100);
        OpacityBox.Text = $"{Math.Round(s.PanelOpacity * 100)}";
        _settingsRefreshing = false;

        DockVisibleSwitch.IsChecked = s.ShowDock;
        DockSizeBox.Text = s.DockSize.ToString();
        DockOpacityBox.Text = $"{Math.Round(s.DockOpacity * 100)}";
        DockRadiusBox.Text = s.DockRadius.ToString("0.##");
        CustomColorSwatch.Background = new SolidColorBrush(ParseColor(s.DockColor));

        PeekBox.Text = (s.PeekDelayMs / 1000.0).ToString("0.##");

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
            HotkeyHint.Text = DefaultHotkeyHint(s.HotkeyEnabled);

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

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_settingsRefreshing || OpacityBox is null) return;
        App.Current.SetPanelOpacity(e.NewValue / 100.0);
        _settingsRefreshing = true;
        OpacityBox.Text = $"{Math.Round(e.NewValue)}";
        _settingsRefreshing = false;
    }

    private void OnNumberKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void OnNumberLostFocus(object sender, RoutedEventArgs e)
    {
        if (_settingsRefreshing || sender is not TextBox box) return;

        switch (box.Name)
        {
            case nameof(ColumnsBox): ApplyInt(box, v => App.Current.SetColumns(v)); break;
            case nameof(RowsBox): ApplyInt(box, v => App.Current.SetMaxRows(v)); break;
            case nameof(IconBox): ApplyInt(box, v => App.Current.SetPanelIconSize(v)); break;
            case nameof(FontBox): ApplyInt(box, v => App.Current.SetPanelFontSize(v)); break;
            case nameof(GapBox): ApplyInt(box, v => App.Current.SetPanelGap(v)); break;
            case nameof(ItemGapBox): ApplyInt(box, v => App.Current.SetPanelItemGap(v)); break;
            case nameof(RecentBox): ApplyInt(box, v => App.Current.SetRecentCount(v)); break;
            case nameof(DockSizeBox): ApplyInt(box, v => App.Current.SetDockSize(v)); break;
            case nameof(DockOpacityBox):
                if (int.TryParse(box.Text.Trim(), out var opacity))
                    App.Current.SetDockOpacity(opacity / 100.0);
                break;
            case nameof(DockRadiusBox):
                if (double.TryParse(box.Text.Trim(), out var radius))
                    App.Current.SetDockRadius(radius);
                break;
            case nameof(SnapBox): ApplyInt(box, v => App.Current.SetSnapDistance(v)); break;
            case nameof(PeekBox):
                if (double.TryParse(box.Text.Trim(), out var seconds))
                    App.Current.SetPeekDelay((int)Math.Round(seconds * 1000));
                break;
            case nameof(OpacityBox):
                if (int.TryParse(box.Text.Trim(), out var percent))
                    App.Current.SetPanelOpacity(percent / 100.0);
                break;
        }

        RefreshSettings();
    }

    private static void ApplyInt(TextBox box, Action<int> apply)
    {
        if (int.TryParse(box.Text.Trim(), out var value)) apply(value);
    }

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

    private void OnCustomColorClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            AnyColor = true,
            Color = ToDrawingColor(ParseColor(App.Current.Settings.DockColor))
        };

        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (dialog.ShowDialog(new Win32Window(owner)) != System.Windows.Forms.DialogResult.OK) return;

        var chosen = dialog.Color;
        App.Current.SetDockColor($"#{chosen.R:X2}{chosen.G:X2}{chosen.B:X2}");
        RefreshSettings();
    }

    private static Color ParseColor(string value)
    {
        try { return (Color)ColorConverter.ConvertFromString(value); }
        catch { return Colors.White; }
    }

    private static System.Drawing.Color ToDrawingColor(Color color) =>
        System.Drawing.Color.FromArgb(color.R, color.G, color.B);

    private sealed class Win32Window : System.Windows.Forms.IWin32Window
    {
        public Win32Window(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
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
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
            return;

        if (key == Key.Escape)
        {
            CancelHotkeyCapture();
            return;
        }

        var mods = Keyboard.Modifiers;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        // Modifier-free hotkeys are fine for function/media/navigation keys; for anything you
        // need while typing, say why instead of silently ignoring the press.
        if (mods == ModifierKeys.None && !HotkeyService.AllowsStandalone(vk))
        {
            HotkeyHint.Text = HotkeyService.ExplainStandaloneRefusal(key) ?? "请至少加一个修饰键";
            HotkeyText.Text = "按下组合键…";
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
        HotkeyHint.Text = DefaultHotkeyHint(App.Current.Settings.HotkeyEnabled);
        HotkeyText.Text = App.Current.Settings.Hotkey;
    }

    /// <summary>The hint sits on the same line as the switch, so it has to stay short.</summary>
    private static string DefaultHotkeyHint(bool enabled) =>
        enabled ? HotkeyHintDefault : "默认关闭，打开后生效";

    private void OnSettingsClose(object sender, RoutedEventArgs e) => Close();

    public void NavigateTo(string name)
    {
        AppearanceCard.Visibility = name == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        LayoutCard.Visibility = name == "layout" ? Visibility.Visible : Visibility.Collapsed;
        DockCard.Visibility = name == "dock" ? Visibility.Visible : Visibility.Collapsed;
        StartupCard.Visibility = name == "startup" ? Visibility.Visible : Visibility.Collapsed;
        var advanced = name is "search" or "files" or "extensions" or "excludes" or "engines";
        AdvancedDetails.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
        if (advanced) AdvancedDetails.ShowModule(name);

        foreach (var button in SettingsNavList.Children.OfType<Button>())
        {
            var selected = string.Equals(button.Tag as string, name, StringComparison.Ordinal);
            // Resource references, not resolved brushes: the nav must follow a theme switch the
            // moment it happens instead of keeping the palette it was painted with.
            if (selected)
            {
                button.SetResourceReference(Control.BackgroundProperty, "SelectedBrush");
                button.SetResourceReference(Control.ForegroundProperty, "InkBrush");
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.ForegroundProperty);
            }
        }
        SettingsScroll.ScrollToTop();
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string name }) NavigateTo(name);
    }

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
