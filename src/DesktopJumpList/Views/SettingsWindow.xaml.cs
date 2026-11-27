using System.Diagnostics;
using System.IO;
using System.Threading;
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
        // Safety net for the bounded lists inside the page (see WheelScroller.AttachNearest).
        WheelScroller.AttachNearest(this, SettingsScroll);

        // A download must not outlive the window it reports into.
        Closed += (_, _) => _downloadCts?.Cancel();
        SourceInitialized += (_, _) => CenterOnScreen();
        Loaded += (_, _) =>
        {
            RefreshSettings();
            SettingsScroll.ScrollToTop();
            GrowToFitContent();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDown;
        // Native WM_NCHITTEST drag: non-interactive surface returns HTCAPTION and Windows runs
        // the drag loop (same mechanism as a normal title bar). DragMove-in-MouseLeftButtonDown
        // proved unreliable here; the window stays borderless and shadow-free.
        NativeSurfaceDrag.Enable(this, HitsInteractive);
        DownloadChart.SizeChanged += (_, _) => DrawDownloadChart();
    }

    private static bool HitsInteractive(DependencyObject? node)
    {
        while (node is not null)
        {
            // Scrollable zones (the bounded lists) must answer HTCLIENT, not HTCAPTION: Windows
            // treats a caption-area point as title bar and swallows the mouse wheel entirely, so
            // no WPF handler ever sees it — the reason those lists "could not scroll" while every
            // Control around them scrolled fine.
            if (node is FrameworkElement { Tag: "ScrollZone" }) return true;
            // Window derives from Control, so without this stop the walk always ends up
            // "interactive" and no point of the window is ever draggable. Reaching the window
            // means the cursor sits on plain background/card chrome, which should drag.
            if (node is Window) return false;
            // Skips MUST come before the Control check: ScrollViewer (the card stack's scroll
            // host) and UserControl (the whole advanced-settings page) are Controls themselves.
            // Checking `is Control` first would short-circuit every click in the content area
            // and the window would never drag — that was the live bug.
            // The whole card area scrolls inside one ScrollViewer — it is a Control itself
            // but must not swallow the drag; its ScrollBars are Controls and still will.
            if (node is System.Windows.Controls.ScrollViewer)
            { node = System.Windows.Media.VisualTreeHelper.GetParent(node); continue; }
            // The advanced pages are one UserControl; walk past it to the real controls inside.
            if (node is System.Windows.Controls.UserControl)
            { node = System.Windows.Media.VisualTreeHelper.GetParent(node); continue; }
            // Control covers buttons, text boxes, sliders, switches, radio dots, list boxes...
            if (node is System.Windows.Controls.Control) return true;
            if (node is FrameworkElement { Name: nameof(HotkeyBox) }) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>Preview/self-test hook: would a press at this screen point drag the window?</summary>
    internal bool WouldDragAt(Point screenPoint) => NativeSurfaceDrag.WouldDrag(this, HitsInteractive, screenPoint);

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

        SearchBoxSwitch.IsChecked = s.SearchBoxVisible;
        SearchWidthBox.Text = s.SearchBoxWidth.ToString();

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

        HotkeyText.Text = HotkeyService.FormatForDisplay(s.Hotkey);
        HotkeySwitch.IsChecked = s.HotkeyEnabled;
        AutostartSwitch.IsChecked = StartupService.IsEnabled;
        if (!_capturingHotkey)
            HotkeyHint.Text = DefaultHotkeyHint(s.HotkeyEnabled);

        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = version is null ? "Jump" : $"Jump v{version.ToString(3)}";
        AboutVersion.Text = VersionText.Text;
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
            case nameof(SearchWidthBox): ApplyInt(box, v => App.Current.SetSearchBoxWidth(v)); break;
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

    private void OnSearchBoxVisibleClick(object sender, RoutedEventArgs e)
    {
        App.Current.SetSearchBoxVisible(SearchBoxSwitch.IsChecked == true);
        RefreshSettings();
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
                ? $"已更新为 {HotkeyService.FormatForDisplay(text)}"
                : $"已设置为 {HotkeyService.FormatForDisplay(text)}；打开开关后生效";
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
        HotkeyText.Text = HotkeyService.FormatForDisplay(App.Current.Settings.Hotkey);
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
        AboutCard.Visibility = name == "about" ? Visibility.Visible : Visibility.Collapsed;
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

    // ───────────────────────── 关于软件 / 检查更新 ─────────────────────────

    private Services.UpdateService.ReleaseInfo? _pendingUpdate;

    private void OnOpenProjectPage(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Services.UpdateService.RepoUrl) { UseShellExecute = true }); }
        catch { /* no browser association: nothing sensible to fall back to */ }
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        DownloadUpdateButton.Visibility = Visibility.Collapsed;
        _pendingUpdate = null;
        SetUpdateStatus("正在检查更新…");
        try
        {
            var snapshot = await Services.UpdateService.CheckLatestAsync();
            var latest = await Services.UpdateService.GetLatestReleaseAsync();
            var current = typeof(App).Assembly.GetName().Version;
            if (snapshot is null || latest is null || current is null)
            {
                SetUpdateStatus("无法获取版本信息：网络不可用且没有本地缓存，可稍后再试");
                return;
            }
            var notice = UpdateServiceExtensions.CacheNotice(snapshot);
            if (latest.Version <= current)
            {
                SetUpdateStatus($"已是最新版本（v{current.ToString(3)}）{notice}");
                return;
            }
            _pendingUpdate = latest;
            SetUpdateStatus($"发现新版本 {latest.Tag}（当前 v{current.ToString(3)}）{notice}");
            DownloadUpdateButton.Visibility = latest.AssetUrl is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (Exception ex)
        {
            SetUpdateStatus($"检查更新失败：{ex.Message}");
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Update feedback lives directly under the 检查更新 button; the row collapses while there is
    /// nothing to say so it stays the same height as the other rows.
    /// </summary>
    /// <summary>Preview hook: show a canned update result so the row layout can be checked.</summary>
    internal void ShowPreviewUpdateStatus(string text) => SetUpdateStatus(text);

    private void SetUpdateStatus(string text)
    {
        UpdateStatus.Text = text;
        UpdateStatus.ToolTip = string.IsNullOrWhiteSpace(text) ? null : text;
        UpdateStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private CancellationTokenSource? _downloadCts;

    private void OnCancelUpdateClick(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
        CancelUpdateButton.IsEnabled = false;
        SetUpdateStatus("正在取消…");
    }

    private async void OnDownloadUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is not { AssetUrl: { } url, AssetFileName: { } name }) return;
        DownloadUpdateButton.IsEnabled = false;
        CancelUpdateButton.IsEnabled = true;
        CancelUpdateButton.Visibility = Visibility.Visible;

        var cts = new CancellationTokenSource();
        _downloadCts = cts;
        var dest = string.Empty;
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            dest = Path.Combine(downloads, name);
            SetUpdateStatus("正在下载…");
            await Services.UpdateService.DownloadAsync(url, dest,
                percent => Dispatcher.Invoke(() => SetUpdateStatus($"正在下载… {percent}%")), cts.Token);
            SetUpdateStatus($"下载完成：{dest}");
            CancelUpdateButton.Visibility = Visibility.Collapsed;
            if (MessageBox.Show(this, $"安装包已下载到：\n{dest}\n\n立即运行安装？",
                    "Jump 更新", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                Process.Start(new ProcessStartInfo(dest) { UseShellExecute = true });
                // The installer replaces files under {app}; a resident Jump would keep them locked
                // and only make the setup ask (or fail) to close the running app. Step aside.
                _ = Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            DownloadUpdateButton.Visibility = Visibility.Collapsed;
            _pendingUpdate = null;
        }
        catch (OperationCanceledException)
        {
            SetUpdateStatus("已取消下载");
            // The partial file is useless and would only confuse the next attempt.
            try { File.Delete(dest + ".download"); } catch { }
        }
        catch (Exception ex)
        {
            SetUpdateStatus($"下载失败：{ex.Message}");
            try { File.Delete(dest + ".download"); } catch { }
        }
        finally
        {
            if (ReferenceEquals(_downloadCts, cts)) _downloadCts = null;
            cts.Dispose();
            CancelUpdateButton.Visibility = Visibility.Collapsed;
            CancelUpdateButton.IsEnabled = true;
            DownloadUpdateButton.IsEnabled = true;
        }
    }

    // ───────────────────────── 下载统计折线图 ─────────────────────────

    private List<Services.UpdateService.DownloadStat>? _downloadStats;
    private bool _loadingDownloadStats;

    private void OnRefreshDownloadStatsClick(object sender, RoutedEventArgs e)
    {
        if (_loadingDownloadStats) return;
        _loadingDownloadStats = true;
        DownloadStatsStatus.Text = "正在加载…";
        DownloadStatsStatus.Visibility = Visibility.Visible;
        DownloadChart.Children.Clear();
        _ = LoadDownloadStatsAsync();
    }

    private async Task LoadDownloadStatsAsync()
    {
        try
        {
            var snapshot = await Services.UpdateService.GetDownloadStatsAsync();
            if (snapshot is null)
            {
                DownloadStatsStatus.Text = "加载失败：网络不可用且没有本地缓存";
                DownloadStatsStatus.Visibility = Visibility.Visible;
                return;
            }
            var stats = new List<Services.UpdateService.DownloadStat>();
            foreach (var r in snapshot.Releases)
                stats.Add(new Services.UpdateService.DownloadStat(r.Tag, r.Version, r.Downloads));
            stats.Sort((a, b) => (a.Version ?? new Version()).CompareTo(b.Version ?? new Version()));

            _downloadStats = stats;
            DownloadStatsStatus.Visibility = Visibility.Collapsed;
            DownloadSummary.Text = $"共 {stats.Count} 个版本，累计 {stats.Sum(s => s.Downloads)} 次下载"
                + UpdateServiceExtensions.StatsNotice(snapshot);
            DrawDownloadChart();
        }
        catch (Exception ex)
        {
            DownloadStatsStatus.Text = $"加载失败：{ex.Message}";
            DownloadStatsStatus.Visibility = Visibility.Visible;
        }
        finally
        {
            _loadingDownloadStats = false;
        }
    }

    /// <summary>Preview hook (--render-preview): fill the chart with synthetic multi-release
    /// data so the axis layout can be verified offline. Tags are derived from the running
    /// assembly version — nothing here is hardcoded, so it never goes stale.</summary>
    internal void ShowPreviewStats()
    {
        var v = GetType().Assembly.GetName().Version ?? new Version(1, 0, 0);
        const int count = 12;
        var start = Math.Max(1, Math.Max(1, v.Build) - (count - 1));
        var stats = new List<Services.UpdateService.DownloadStat>();
        var rng = new Random(7);
        var acc = 20;
        for (var i = 0; i < count; i++)
        {
            acc += rng.Next(5, 40);
            var patch = start + i;
            stats.Add(new Services.UpdateService.DownloadStat(
                $"v{v.Major}.{v.Minor}.{patch}",
                new Version(v.Major, v.Minor, patch),
                acc));
        }
        _downloadStats = stats;
        DownloadStatsStatus.Visibility = Visibility.Collapsed;
        DrawDownloadChart();
    }

    /// <summary>Draws the per-release download-count line on <see cref="DownloadChart"/>.
    /// Redrawn on refresh and whenever the card resizes (it starts collapsed → zero size).</summary>
    private void DrawDownloadChart()
    {
        var stats = _downloadStats;
        if (stats is null || stats.Count == 0) return;

        DownloadChart.Children.Clear();
        var w = DownloadChart.ActualWidth;
        var h = DownloadChart.ActualHeight;
        if (w < 60 || h < 60) return;

        var accent = (Brush)FindResource("AccentBrush");
        var hint = (Style)FindResource("InlineHint");
        var grid = new SolidColorBrush(Color.FromArgb(38, 128, 128, 128));

        double left = 44, right = 12, top = 16, bottom = 32;
        var plotW = Math.Max(10, w - left - right);
        var plotH = Math.Max(10, h - top - bottom);

        var maxV = Math.Max(1, stats.Max(s => s.Downloads));
        var step = NiceStep(maxV);
        var maxTick = Math.Max(step, (int)Math.Ceiling(maxV / (double)step) * step);

        for (var v = 0; v <= maxTick; v += step)
        {
            var y = top + plotH * (1 - v / (double)maxTick);
            DownloadChart.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = left, X2 = w - right, Y1 = y, Y2 = y,
                Stroke = grid, StrokeThickness = 1, SnapsToDevicePixels = true
            });
            var label = new TextBlock { Text = v.ToString(), Style = hint, FontSize = 10 };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, left - 8 - label.DesiredSize.Width);
            Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
            DownloadChart.Children.Add(label);
        }

        var points = new List<Point>(stats.Count);
        for (var i = 0; i < stats.Count; i++)
        {
            var x = stats.Count == 1 ? left + plotW / 2 : left + plotW * i / (stats.Count - 1);
            var y = top + plotH * (1 - stats[i].Downloads / (double)maxTick);
            points.Add(new Point(x, y));
        }

        var line = new System.Windows.Shapes.Polyline
        {
            Points = new PointCollection(points),
            Stroke = accent, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round
        };
        DownloadChart.Children.Add(line);

        // x labels: decide the visible set BEFORE creating any TextBlock. Neighbours need a
        // full tag width plus real breathing room (the old 52px floor let ~10 tags pile up),
        // the first and last tag are always shown, and the greedy pass drops crowded middle
        // tags — including one that would collide with the mandatory last tag.
        double ClampX(int i, double tagWidth) =>
            Math.Min(Math.Max(points[i].X - tagWidth / 2, 2), w - tagWidth - 2);

        double WidthOf(string tag)
        {
            var t = new TextBlock { Text = tag, Style = hint, FontSize = 10 };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return t.DesiredSize.Width;
        }

        var tagWidth = WidthOf(stats.Count > 0 ? stats[0].Tag : "v0.0.0");
        var stride = Math.Max(1, (int)Math.Ceiling((tagWidth + 24) * Math.Max(1, stats.Count - 1) / plotW));
        var candidates = new List<int>();
        for (var i = 0; i < stats.Count; i += stride) candidates.Add(i);
        if (candidates[^1] != stats.Count - 1) candidates.Add(stats.Count - 1);

        var kept = new List<int>();
        for (var c = 0; c < candidates.Count; c++)
        {
            var idx = candidates[c];
            var cw = WidthOf(stats[idx].Tag);
            var cLeft = ClampX(idx, cw);
            var cRight = cLeft + cw;
            if (kept.Count > 0)
            {
                var prev = kept[^1];
                var prevW = WidthOf(stats[prev].Tag);
                var prevLeft = ClampX(prev, prevW);
                var prevRight = prevLeft + prevW;
                if (cLeft < prevRight + 6)
                {
                    // The last tag must always show; if it collides, sacrifice the neighbour
                    // in front of it (never the first tag).
                    if (c == candidates.Count - 1 && kept.Count > 1) { kept.RemoveAt(kept.Count - 1); }
                    else continue;
                }
            }
            kept.Add(idx);
        }

        foreach (var i in kept)
        {
            var tag = stats[i].Tag;
            var label = new TextBlock { Text = tag, Style = hint, FontSize = 10 };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, ClampX(i, label.DesiredSize.Width));
            Canvas.SetTop(label, top + plotH + 6);
            DownloadChart.Children.Add(label);
        }

        for (var i = 0; i < stats.Count; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 8, Height = 8, Fill = accent,
                ToolTip = $"{stats[i].Tag}: {stats[i].Downloads} 次"
            };
            Canvas.SetLeft(dot, points[i].X - 4);
            Canvas.SetTop(dot, points[i].Y - 4);
            DownloadChart.Children.Add(dot);
        }

        // No per-release count annotation on the plot: the shape of the line is the message,
        // and a floating "v3.14.x · N 次" tag only crowded the newest point.
    }

    /// <summary>Smallest of 1/2/5×10^n keeping the y axis at ≤ 5 ticks.</summary>
    private static int NiceStep(int max)
    {
        foreach (var base0 in new[] { 1, 2, 5 })
            for (var e = 0; e < 5; e++)
            {
                var s = base0 * (int)Math.Pow(10, e);
                if (max / s <= 4) return s;
            }
        return Math.Max(1, max);
    }

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
