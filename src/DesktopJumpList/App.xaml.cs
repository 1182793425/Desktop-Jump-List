using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DesktopJumpList.Models;
using DesktopJumpList.Services;
using DesktopJumpList.Views;

namespace DesktopJumpList;

public partial class App : Application
{
    private const string InstanceName = "DesktopJumpList.Jump.v3";
    private Mutex? _mutex;
    private EventWaitHandle? _wakeSignal;
    private HotkeyService? _hotkey;

    public Storage Storage { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public ShortcutLibrary Library { get; private set; } = null!;
    public DockWindow Dock { get; private set; } = null!;
    public LauncherWindow Launcher { get; private set; } = null!;
    public TrayIcon? Tray { get; private set; }

    public bool IsShuttingDown { get; private set; }

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0 && e.Args[0].Equals("--render-preview", StringComparison.OrdinalIgnoreCase))
        {
            var dir = e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "JumpPreview");
            _ = RenderPreviewAsync(dir);
            return;
        }

        // Single instance: a second launch just pops the existing panel open.
        _mutex = new Mutex(true, InstanceName, out var isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(InstanceName + ".wake").Set(); } catch { }
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        try
        {
            Boot();
        }
        catch (Exception ex)
        {
            Log(ex);
            throw;
        }
    }

    private void Boot()
    {
        Storage = new Storage();
        Settings = Storage.LoadSettings();
        Settings.PanelOpacity = Math.Clamp(Settings.PanelOpacity, 0.0, 1.0);
        if (!IsHexColor(Settings.DockColor)) Settings.DockColor = "#FFFFFF";
        if (!HotkeyService.TryParse(Settings.Hotkey, out _, out _)) Settings.Hotkey = "Ctrl+Alt+J";
        ThemeService.Initialize(Settings.Theme);
        ThemeService.SetPanelOpacity(Settings.PanelOpacity);
        Library = new ShortcutLibrary(Storage);

        Launcher = new LauncherWindow(Library);
        Dock = new DockWindow(Settings);
        if (Settings.ShowDock) Dock.Show();

        // The app has no taskbar button by design; the tray icon is its permanent home.
        Tray = new TrayIcon(this);

        _hotkey = new HotkeyService();
        _hotkey.Pressed += () => ToggleLauncher(fromHotkey: true);
        if (Settings.HotkeyEnabled)
        {
            if (!_hotkey.Register(Settings.Hotkey))
            {
                Settings.HotkeyEnabled = false;
                SaveSettings();
                Launcher.ShowNotice($"全局快捷键 {Settings.Hotkey} 注册失败（可能被其他程序占用），已自动关闭。可在设置里更换或重新启用。");
            }
        }

        _wakeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".wake");
        var wakeThread = new Thread(() =>
        {
            while (_wakeSignal.WaitOne())
            {
                if (IsShuttingDown) break;
                try { Dispatcher.BeginInvoke(() => ShowLauncher()); }
                catch { break; }
            }
        }) { IsBackground = true };
        wakeThread.Start();

        _ = IconService.LoadAsync(Library.Items);
        Dispatcher.BeginInvoke(ScheduleIdleTrim, DispatcherPriority.ApplicationIdle);

        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            SaveSettings();
            // First run: show the panel so the empty state explains how to add things, and
            // point out where the app now lives (there is deliberately no taskbar button).
            Dispatcher.BeginInvoke(() =>
            {
                ShowLauncher();
                Tray?.ShowBalloon("Jump 已在系统托盘运行：左键托盘图标打开面板，右键可设置。");
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    // ───────────────────────── Windows ─────────────────────────

    public void ToggleLauncher(bool fromHotkey = false)
    {
        if (IsShuttingDown || Launcher.IsModal) return;
        if (Launcher.IsOpen) Launcher.Close(animated: true);
        else ShowLauncher(revealDock: !fromHotkey);
    }

    public void ShowLauncher(bool revealDock = true)
    {
        if (IsShuttingDown || Launcher.IsModal) return;
        if (revealDock && Settings.ShowDock) Dock.HoldOpen(true);
        Launcher.Open();
    }

    private SettingsWindow? _settingsWindow;
    private AdvancedSettingsWindow? _advancedWindow;

    /// <summary>Opens the advanced page on its own (used by the "/advanced" palette command).</summary>
    public void OpenAdvancedSettings()
    {
        if (IsShuttingDown) return;
        if (_advancedWindow is { IsVisible: true })
        {
            _advancedWindow.Activate();
            return;
        }

        _advancedWindow = new AdvancedSettingsWindow();
        _advancedWindow.Closed += (_, _) => _advancedWindow = null;
        _advancedWindow.Show();
        _advancedWindow.Activate();
    }

    /// <summary>Opens the standalone settings window, centered on the screen. The panel stays closed.</summary>
    public void OpenSettings()
    {
        if (IsShuttingDown || Launcher.IsModal) return;

        if (Launcher.IsOpen) Launcher.Close(animated: false);

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; ScheduleIdleTrim(); };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Called by the launcher once it has fully faded out.</summary>
    public void OnLauncherClosed()
    {
        if (Settings.ShowDock) Dock.HoldOpen(false);
        ScheduleIdleTrim();
    }

    // ───────────────────────── Idle housekeeping ─────────────────────────

    private DispatcherTimer? _trimTimer;

    /// <summary>
    /// A tray-resident WPF app keeps whatever the last panel used in its heap. Once the app has
    /// been quiet for a few seconds, hand those pages back so the idle footprint is the tray icon,
    /// not the panel you closed a minute ago. Cancelled the moment anything opens again.
    /// </summary>
    private void ScheduleIdleTrim()
    {
        if (IsShuttingDown) return;

        if (_trimTimer is null)
        {
            _trimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _trimTimer.Tick += (_, _) =>
            {
                _trimTimer!.Stop();
                if (Launcher is null || Launcher.IsOpen || Launcher.IsModal) return;
                if (_settingsWindow is { IsVisible: true }) return;
                TrimWorkingSet();
            };
        }

        _trimTimer.Stop();
        _trimTimer.Start();
    }

    private static void TrimWorkingSet()
    {
        try
        {
            // Only the resident set is handed back; the pages stay in the standby list, so the
            // next open re-faults them from RAM instead of disk. No GC.Collect: it costs time and
            // does not shrink the working set on its own.
            using var process = Process.GetCurrentProcess();
            Infrastructure.Native.EmptyWorkingSet(process.Handle);
        }
        catch { }
    }

    /// <summary>Shows a short message in the panel; if it is closed, it appears at the next open.</summary>
    public void Notify(string text)
    {
        if (Launcher is null) return;
        if (!Launcher.IsOpen && Tray is not null) Tray.ShowBalloon(text);
        Launcher.ShowNotice(text);
    }

    // ───────────────────────── Settings ─────────────────────────

    public void SaveSettings()
    {
        try { Storage.SaveSettings(Settings); }
        catch (Exception ex) { Log(ex); }
    }

    public void SetTheme(AppTheme mode)
    {
        Settings.Theme = mode;
        ThemeService.SetMode(mode);
        SaveSettings();
    }

    public void SetColumns(int columns)
    {
        Settings.Columns = columns;
        SaveSettings();
        Launcher.ApplyLayout();
    }

    public void SetMaxRows(int rows)
    {
        Settings.MaxRows = rows;
        SaveSettings();
        Launcher.ApplyLayout();
    }

    public void SetPeekDelay(int milliseconds)
    {
        Settings.PeekDelayMs = milliseconds;
        SaveSettings();
        Dock.ApplySettings();
    }

    public void SetShowDock(bool show)
    {
        Settings.ShowDock = show;
        SaveSettings();
        Dock.ApplySettings();
        if (show && Launcher.IsOpen) Dock.HoldOpen(true);
    }

    public void SetDockSize(int size)
    {
        Settings.DockSize = size;
        SaveSettings();
        Dock.ApplySettings();
    }

    public void SetDockColor(string color)
    {
        color = color.ToUpperInvariant();
        if (!IsHexColor(color)) return;
        Settings.DockColor = color;
        SaveSettings();
        Dock.ApplySettings();
    }

    public void SetDockOpacity(double opacity)
    {
        Settings.DockOpacity = Math.Clamp(opacity, 0.0, 1.0);
        SaveSettings();
        Dock.ApplySettings();
    }

    public void SetDockRadius(double radius)
    {
        Settings.DockRadius = Math.Max(0, radius);
        SaveSettings();
        Dock.ApplySettings();
    }

    public void SetPanelIconSize(int size)
    {
        Settings.PanelIconSize = size;
        SaveSettings();
        Launcher.ApplyPanelStyle();
    }

    public void SetPanelFontSize(int size)
    {
        Settings.PanelFontSize = size;
        SaveSettings();
        Launcher.ApplyPanelStyle();
    }

    public void SetPanelGap(int gap)
    {
        Settings.PanelGap = gap;
        SaveSettings();
        Launcher.ApplyPanelStyle();
    }

    public void SetSearchEngines(IEnumerable<string> ids)
    {
        Settings.SearchEngines = ids.ToList();
        SaveSettings();
        if (Launcher.IsOpen) Launcher.ApplyPanelStyle();
    }

    public void SetPanelItemGap(int gap)
    {
        Settings.PanelItemGap = gap;
        SaveSettings();
        Launcher.ApplyPanelStyle();
    }

    public void SetRecentCount(int count)
    {
        Settings.RecentCount = count;
        SaveSettings();
        Launcher.ApplyPanelStyle();
    }

    public void SetSnapDistance(int distance)
    {
        Settings.SnapDistance = distance;
        SaveSettings();
    }

    public void SetPanelOpacity(double opacity)
    {
        Settings.PanelOpacity = Math.Clamp(opacity, 0.0, 1.0);
        SaveSettings();
        ThemeService.SetPanelOpacity(Settings.PanelOpacity);
    }

    /// <summary>Returns an error message, or null on success.</summary>
    public string? SetAutostart(bool enabled)
    {
        try
        {
            StartupService.Set(enabled);
            return null;
        }
        catch (Exception ex)
        {
            Log(ex);
            return ex.Message;
        }
    }

    /// <summary>Re-registers the global hotkey. The old one is restored if the new one is taken.</summary>
    public bool TrySetHotkey(string text, out string? error)
    {
        error = null;
        if (_hotkey is null) { error = "快捷键服务未就绪"; return false; }
        if (!HotkeyService.TryParse(text, out _, out _)) { error = "请至少使用 Ctrl / Alt / Shift / Win 中的一个组合键"; return false; }

        var previous = Settings.Hotkey;
        if (Settings.HotkeyEnabled && !_hotkey.Register(text))
        {
            _hotkey.Register(previous);
            error = $"「{text}」已被其他程序占用";
            return false;
        }

        Settings.Hotkey = text;
        SaveSettings();
        return true;
    }

    /// <summary>Turns the global hotkey on/off. Enabling can fail if the combination is taken.</summary>
    public bool SetHotkeyEnabled(bool enabled, out string? error)
    {
        error = null;
        if (_hotkey is null) { error = "快捷键服务未就绪"; return false; }

        if (enabled)
        {
            if (!HotkeyService.TryParse(Settings.Hotkey, out _, out _))
            {
                error = "快捷键格式不正确";
                return false;
            }
            if (!_hotkey.Register(Settings.Hotkey))
            {
                Settings.HotkeyEnabled = false;
                SaveSettings();
                error = $"「{Settings.Hotkey}」已被其他程序占用，换一个组合键再试";
                return false;
            }
            Settings.HotkeyEnabled = true;
        }
        else
        {
            _hotkey.Unregister();
            Settings.HotkeyEnabled = false;
        }

        SaveSettings();
        return true;
    }

    public void OpenDataFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Storage.Directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log(ex); }
    }

    private static bool IsHexColor(string? value) =>
        value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit);

    public void Quit()
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        try { Library.SaveNow(); } catch { }
        SaveSettings();
        _hotkey?.Dispose();
        _hotkey = null;
        Tray?.Dispose();
        Tray = null;
        Dock?.Hide();
        Shutdown();
    }

    // ───────────────────────── Lifecycle ─────────────────────────

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log(e.Exception);
        e.Handled = true; // A launcher must never crash away; log and keep running.
    }

    public void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Storage?.Directory ?? Path.GetTempPath(), "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Library?.SaveNow(); } catch { }
        _hotkey?.Dispose();
        Tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    // ───────────────────────── Developer preview ─────────────────────────

    /// <summary>
    /// Hidden `--render-preview &lt;dir&gt;` mode: renders the dock, launcher and settings
    /// surfaces to PNG with RenderTargetBitmap. Useful when working on the visual design
    /// without an interactive desktop.
    /// </summary>
    private async Task RenderPreviewAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            // Keep the preview completely away from the real %LocalAppData% data.
            Storage = new Storage(Path.Combine(directory, "_preview-data"));
            Settings = Storage.LoadSettings();
            ThemeService.Initialize(AppTheme.Dark);
            Library = new ShortcutLibrary(Storage);
            SeedPreviewItems();

            Launcher = new LauncherWindow(Library);
            Dock = new DockWindow(Settings) { PreviewMode = true };
            Launcher.Show();
            Dock.Show();
            await Task.Delay(350);

            foreach (var mode in new[] { AppTheme.Dark, AppTheme.Light })
            {
                // Preview only: apply the palette without touching the persisted settings.
                Settings.Theme = mode;
                ThemeService.SetMode(mode);
                await Task.Delay(220);

                Launcher.Open();
                await Task.Delay(500);
                CaptureLauncher(Path.Combine(directory, $"launcher-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                CaptureDock(Path.Combine(directory, $"dock-{mode.ToString().ToLowerInvariant()}.png"), 4);

                Launcher.Query.Text = "微";
                await Task.Delay(450);
                CaptureLauncher(Path.Combine(directory, $"search-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                Launcher.Query.Text = string.Empty;

                // "/" palette: the command list and a computed result.
                Launcher.Query.Text = "/";
                await Task.Delay(900);
                CaptureLauncher(Path.Combine(directory, $"slash-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                Launcher.Query.Text = "/calc 12*(8+3)";
                await Task.Delay(420);
                CaptureLauncher(Path.Combine(directory, $"slash-calc-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                Launcher.Query.Text = string.Empty;

                var settingsWindow = new SettingsWindow();
                settingsWindow.Show();
                await Task.Delay(500);
                RenderElement(settingsWindow, Path.Combine(directory, $"settings-{mode.ToString().ToLowerInvariant()}.png"), 1.4);
                DumpSettingsLayout(directory, settingsWindow);

                var advancedWindow = new AdvancedSettingsWindow();
                advancedWindow.Show();
                await Task.Delay(400);
                RenderElement(advancedWindow, Path.Combine(directory, $"advanced-{mode.ToString().ToLowerInvariant()}.png"), 1.4);
                advancedWindow.Close();
                settingsWindow.Close();
                Launcher.Close(animated: false);
            }

            DumpLayout(directory);

            // Larger panel metrics and a coloured dock, to check both settings live.
            Settings.Theme = AppTheme.Dark;
            ThemeService.SetMode(AppTheme.Dark);
            SetPanelIconSize(48);
            SetPanelFontSize(14);
            SetPanelGap(10);
            Launcher.Open();
            await Task.Delay(500);
            CaptureLauncher(Path.Combine(directory, "launcher-large-dark.png"), 1.5);
            Launcher.Close(animated: false);

            SetDockSize(56);
            SetDockColor("#3B82F6");
            await Task.Delay(450);
            CaptureDock(Path.Combine(directory, "dock-custom.png"), 4);
            SetDockColor("#FFFFFF");
            SetDockSize(48);
            SetPanelIconSize(40);
            SetPanelFontSize(12);
            SetPanelGap(8);

            Dock.PreviewSetTucked(true);
            await Task.Delay(500);
            CaptureDock(Path.Combine(directory, "dock-tucked.png"), 4);

            // Bonus previews: the "more recently used" view and a folder view (back tile).
            Launcher.Open();
            await Task.Delay(300);
            var privateFlags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            typeof(LauncherWindow).GetMethod("OpenRecentView", privateFlags)?.Invoke(Launcher, null);
            await Task.Delay(400);
            CaptureLauncher(Path.Combine(directory, "launcher-recent-all-dark.png"), 1.5);
            typeof(LauncherWindow).GetMethod("OpenFolder", privateFlags)?.Invoke(Launcher, new object?[] { "设计" });
            await Task.Delay(400);
            CaptureLauncher(Path.Combine(directory, "launcher-folder-dark.png"), 1.5);
            typeof(LauncherWindow).GetMethod("OpenFolder", privateFlags)?.Invoke(Launcher, new object?[] { null });
            typeof(LauncherWindow).GetMethod("EnterEditMode", privateFlags)?.Invoke(Launcher, null);
            await Task.Delay(450);
            CaptureLauncher(Path.Combine(directory, "launcher-edit-dark.png"), 1.5);
            // Drop preview: the placeholder cell that opens where a dragged tile would land.
            if (Launcher.Tiles.ItemsSource is System.Collections.ObjectModel.ObservableCollection<object> tileItems)
            {
                tileItems.Insert(2, InsertMarker.Instance);
                typeof(LauncherWindow).GetMethod("SyncHeightWithMarker", privateFlags)?.Invoke(Launcher, null);
                await Task.Delay(350);
                CaptureLauncher(Path.Combine(directory, "launcher-drop-dark.png"), 1.5);
                tileItems.Remove(InsertMarker.Instance);
            }

            typeof(LauncherWindow).GetMethod("ExitEditMode", privateFlags)?.Invoke(Launcher, null);
            Launcher.Close(animated: false);

            Settings.Theme = AppTheme.Light;
            ThemeService.SetMode(AppTheme.Light);
            var previewTray = new TrayIcon(this);
            await Task.Delay(300);
            previewTray.RenderPreview(Path.Combine(directory, "tray-menu-light.png"));
            Settings.Theme = AppTheme.Dark;
            ThemeService.SetMode(AppTheme.Dark);
            await Task.Delay(200);
            previewTray.RenderPreview(Path.Combine(directory, "tray-menu-dark.png"));
            previewTray.Dispose();

            Console.WriteLine($"Preview written to {directory}");
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(directory, "preview-error.log"), ex.ToString()); } catch { }
            Console.WriteLine(ex);
        }
        finally
        {
            IsShuttingDown = true;
            Shutdown();
        }
    }

    private void SeedPreviewItems()
    {
        var samples = new (string Name, ShortcutKind Kind, string Folder)[]
        {
            ("Visual Studio Code", ShortcutKind.App, null!),
            ("微信", ShortcutKind.App, null!),
            ("Figma", ShortcutKind.App, null!),
            ("Windows Terminal", ShortcutKind.App, null!),
            ("记事本", ShortcutKind.App, null!),
            ("Microsoft Edge", ShortcutKind.App, null!),
            ("截图工具", ShortcutKind.App, null!),
            ("Notion", ShortcutKind.App, null!),
            ("Spotify", ShortcutKind.App, null!),
            ("下载", ShortcutKind.Folder, null!),
            ("项目文档", ShortcutKind.Folder, null!),
            ("设计稿", ShortcutKind.Folder, "设计"),
            ("素材库", ShortcutKind.Folder, "设计"),
            ("情绪板", ShortcutKind.File, "设计"),
            ("品牌规范", ShortcutKind.File, "设计"),
            ("周报", ShortcutKind.File, "工作"),
            ("需求评审", ShortcutKind.File, "工作"),
            ("GitHub", ShortcutKind.Url, null!)
        };
        foreach (var (name, kind, folder) in samples)
        {
            var item = new ShortcutItem
            {
                Name = name,
                Kind = kind,
                Target = kind == ShortcutKind.Url ? "https://github.com" : $@"C:\JumpPreview\{name}",
                Folder = folder
            };
            if (name == "Figma") item.IsUnavailable = true;
            if (name == "Notion") item.Pinned = true;

            // Preview only: seed a believable "recently used" row.
            var rank = Array.IndexOf(new[] { "Visual Studio Code", "微信", "Figma", "Windows Terminal", "记事本", "Microsoft Edge", "Notion" }, name);
            if (rank >= 0)
            {
                item.LastOpenedAt = DateTime.Now.AddMinutes(-(rank + 1) * 9);
                item.OpenCount = 8 - rank;
            }

            Library.Items.Add(item);
        }
    }

    private void DumpLayout(string directory)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            var dpi = VisualTreeHelper.GetDpi(Launcher);
            sb.AppendLine($"dpi={dpi.DpiScaleX}x{dpi.DpiScaleY}");
            sb.AppendLine($"dock: actual={Dock.ActualWidth:0.##}x{Dock.ActualHeight:0.##} root={Dock.Root.ActualWidth:0.##}x{Dock.Root.ActualHeight:0.##} " +
                          $"rootOpacity={Dock.Root.Opacity:0.##} ballOpacity={Dock.Ball.Opacity:0.##} visible={Dock.IsVisible}");

            void Dump(string label, FrameworkElement element)
            {
                var pos = element.TransformToAncestor(Launcher).Transform(new System.Windows.Point(0, 0));
                sb.AppendLine($"{label}: actual={element.ActualWidth:0.##}x{element.ActualHeight:0.##} " +
                              $"desired={element.DesiredSize.Width:0.##}x{element.DesiredSize.Height:0.##} " +
                              $"pos={pos.X:0.##},{pos.Y:0.##} margin={element.Margin}");
            }

            Dump("window", Launcher);
            if (Launcher.Content is FrameworkElement content) Dump("content", content);
            Dump("canvas", Launcher.Canvas);
            Dump("stack", Launcher.Stack);
            Dump("panel", Launcher.Panel);
            Dump("tiles", Launcher.Tiles);
            if (Launcher.Tiles.Items.Count > 0 &&
                Launcher.Tiles.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first)
                Dump("tile0", first);

            File.WriteAllText(Path.Combine(directory, "layout.txt"), sb.ToString());
        }
        catch { }
    }

    private void CaptureLauncher(string path, double scale) => RenderElement(Launcher, path, scale);

    private void CaptureDock(string path, double scale) => RenderElement(Dock.Root, path, scale);

    /// <summary>Settings metrics: how tall the card stack really is versus the window it sits in.</summary>
    private static void DumpSettingsLayout(string directory, SettingsWindow window)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"window={window.ActualWidth:0.##}x{window.ActualHeight:0.##}");
            sb.AppendLine($"viewport={window.SettingsScroll.ActualHeight:0.##} extent={window.SettingsScroll.ExtentHeight:0.##}");
            sb.AppendLine($"chrome={window.ActualHeight - window.SettingsScroll.ActualHeight:0.##}");
            if (window.SettingsScroll.Content is FrameworkElement content)
                sb.AppendLine($"content={content.ActualWidth:0.##}x{content.ActualHeight:0.##}");
            File.WriteAllText(Path.Combine(directory, "settings-layout.txt"), sb.ToString());
        }
        catch { }
    }

    private static void RenderElement(FrameworkElement element, string path, double scale)
    {
        // A window that was just resized renders blank until its layout/animation frame lands.
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var width = element.ActualWidth;
        var height = element.ActualHeight;
        var pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(height * scale));

        var content = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        content.Render(element);

        var composed = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0B, 0x2A, 0x4A)), null, new Rect(0, 0, width, height));
            dc.DrawImage(content, new Rect(0, 0, width, height));
            dc.Pop();
        }
        composed.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(composed));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
