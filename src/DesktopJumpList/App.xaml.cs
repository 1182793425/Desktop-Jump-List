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

        if (e.Args.Length > 0 && e.Args[0].Equals("--onboarding-preview", StringComparison.OrdinalIgnoreCase))
        {
            var dir = e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "JumpPreview");
            _ = RenderOnboardingAsync(dir);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--settings-selftest", StringComparison.OrdinalIgnoreCase))
        {
            var dir = e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "JumpPreview");
            _ = RunSettingsScrollSelfTestAsync(dir);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--drag-selftest", StringComparison.OrdinalIgnoreCase))
        {
            var dir = e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "JumpPreview");
            _ = RunDragSelfTestAsync(dir);
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--apps-selftest", StringComparison.OrdinalIgnoreCase))
        {
            // Headless check for the registry app index: scan + a few searches, printed to stdout.
            var apps = new AppIndex();
            apps.Rebuild();
            Console.WriteLine($"apps={apps.Count}");
            foreach (var app in apps.Snapshot().Take(10))
                Console.WriteLine($"  [{app.Name}] {app.Version} -> {app.ExePath}");
            foreach (var query in new[] { "edge", "微信", "wx", "code", "vscode", "vs code", "vsi", "QQ" })
            {
                var hits = apps.Search(query, 5);
                Console.WriteLine($"search \"{query}\": {string.Join(" | ", hits.Select(h => h.Name))}");
            }
            Shutdown();
            return;
        }

        if (e.Args.Length > 1 && e.Args[0].Equals("--icon-probe", StringComparison.OrdinalIgnoreCase))
        {
            // Diagnostic: every icon pipeline stage for one path, with size and ink extent.
            Console.WriteLine(IconService.DiagnoseSizes(e.Args[1]));
            Shutdown();
            return;
        }

        if (e.Args.Length > 2 && e.Args[0].Equals("--icon-save", StringComparison.OrdinalIgnoreCase))
        {
            var saved = IconService.SaveChosen(e.Args[1], e.Args[2]);
            Console.WriteLine(saved ? $"saved {e.Args[2]}" : "no icon");
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--icon-selftest", StringComparison.OrdinalIgnoreCase))
        {
            // Headless check for the icon pipeline over both app sources.
            IconSelfTest.Run();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--usn-selftest", StringComparison.OrdinalIgnoreCase))
        {
            UsnSelfTest.Run(e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "JumpPreview"));
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--index-selftest", StringComparison.OrdinalIgnoreCase))
        {
            IndexSelfTest.Run(e.Args.Length > 1 ? e.Args[1] : Path.Combine(Path.GetTempPath(), "JumpPreview"));
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("--rebuild-index", StringComparison.OrdinalIgnoreCase))
        {
            RunRebuildIndex(e.Args.Length > 1 ? e.Args[1] : null);
            Shutdown();
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
        EnsureSearchJumpRules();
        // The shipped blacklist used to list drive letters; it is pattern-based now. Upgrade a
        // list nobody touched, and leave an edited one exactly as the user left it.
        if (AppSettings.IsLegacyDefaultExcludes(Settings.FileIndexExcludes))
        {
            Settings.FileIndexExcludes = new List<string>(AppSettings.DefaultIndexExcludes);
            Storage.SaveSettings(Settings);
        }
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

        // The persisted index answers instantly at startup; nothing is walked here. Only if it
        // is missing (first run, or another rule set) does an explicit rebuild create it.
        Files = new FileIndex(Storage.Directory);
        Files.Changed += OnFileIndexChanged;
        FileSync = new FileIndexSync(Files, BuildIndexRules, () => Settings.FileSearchEnabled);
        var rules = BuildIndexRules();
        Task.Run(() =>
        {
            var loaded = false;
            try { loaded = Files.Load(rules); }
            catch (Exception ex) { Log(ex); }

            if (loaded && !IsShuttingDown)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (!IsShuttingDown && Launcher.IsOpen) Launcher.RefreshFileResults();
                }, DispatcherPriority.Background);
            }

            FileSync.Start();
        });

        // Registry app index: scan once, then watch the Uninstall keys for install/uninstall.
        if (Settings.RegistrySearchEnabled) Apps.Start();

        if (!Settings.FirstRunDone)
        {
            // First run on this machine: the onboarding wizard walks the user through drive
            // selection + first build, the hotkey, the plain-search switch and a usage crash
            // course. FirstRunDone commits when the window closes, whatever the exit path.
            Dispatcher.BeginInvoke(() =>
            {
                if (IsShuttingDown) return;
                try
                {
                    var wizard = new OnboardingWindow();
                    wizard.Show();
                    wizard.Activate();
                }
                catch (Exception ex)
                {
                    Log(ex);
                    Settings.FirstRunDone = true;
                    SaveSettings();
                }
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

    /// <summary>In-memory index of the user's folders, used by the search results.</summary>
    public FileIndex Files { get; private set; } = new();

    /// <summary>Keeps <see cref="Files"/> current through the USN journal or directory watchers.</summary>
    public FileIndexSync FileSync { get; private set; } = null!;

    /// <summary>Registry-backed index of installed applications (independent of the file index).</summary>
    public AppIndex Apps { get; } = new();

    /// <summary>Starts or stops the registry app index + its Uninstall-key watchers.</summary>
    public void SetRegistrySearchEnabled(bool enabled)
    {
        Settings.RegistrySearchEnabled = enabled;
        SaveSettings();
        if (enabled) Apps.Start(); else Apps.Dispose();
        Launcher?.DropAppResults();
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>Opens the settings window at the search module (used by "/advanced").</summary>
    public void OpenAdvancedSettings() => OpenSettings("search");

    /// <summary>Opens the standalone settings window, centered on the screen. The panel stays closed.</summary>
    public void OpenSettings() => OpenSettings("appearance");

    public void OpenSettings(string section)
    {
        if (IsShuttingDown || Launcher.IsModal) return;

        if (Launcher.IsOpen) Launcher.Close(animated: false);

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.NavigateTo(section);
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow();
        _settingsWindow.NavigateTo(section);
        _settingsWindow.Closed += (_, _) => { _settingsWindow = null; ScheduleIdleTrim(); };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// Builds the file index in the background — the only way a full walk ever starts. Every
    /// caller is a manual gesture (the search pill's ⟳, Ctrl+Enter, the settings ⟳); live
    /// updates afterwards are incremental and never re-walk the disk.
    /// </summary>
    public void StartFileIndex(Action? onDone = null)
    {
        if (!Settings.FileSearchEnabled) return;
        Files.Start(BuildIndexRules(), onDone);
    }

    /// <summary>The filter rules the index must honour right now (roots, extensions, blacklist).</summary>
    public IndexRules BuildIndexRules()
    {
        var roots = Settings.FileSearchRootsCustomized
            ? Settings.FileSearchRoots
            : FileIndex.DefaultRoots();
        return new IndexRules(roots, Settings.FileRootLimits, Settings.FileIndexExtensions,
            Settings.FileIndexExcludes, Settings.FileSearchLimit, !Settings.FileSearchRootsCustomized);
    }

    /// <summary>
    /// The rules changed, so the stored rows were filtered by an older rule set. They stay
    /// searchable, but a rebuild is scheduled for the next idle moment instead of blocking now.
    /// </summary>
    private void MarkIndexRulesChanged()
    {
        Files.MarkStale();
        FileSync?.RequestRebuild();
    }

    /// <summary>Manual rebuild; returns false when the feature is off or a walk is already running.</summary>
    public bool RebuildFileIndex(Action? onDone = null)
    {
        if (!Settings.FileSearchEnabled || Files.IsBuilding) return false;
        StartFileIndex(onDone);
        return true;
    }

    public void SetFileSearchEnabled(bool enabled)
    {
        Settings.FileSearchEnabled = enabled;
        SaveSettings();
        // Toggling the switch must not start a disk walk: the index is only built by the
        // explicit rebuild button. It does start or stop the live sync.
        FileSync?.Nudge();
        if (Launcher?.IsOpen == true) Launcher.ApplyPanelStyle();
    }

    public void SetFileSearchOnPlainQuery(bool enabled)
    {
        Settings.FileSearchOnPlainQuery = enabled;
        SaveSettings();
        if (Launcher?.IsOpen == true) Launcher.ApplyPanelStyle();
    }

    public void SetSearchBoxVisible(bool visible)
    {
        Settings.SearchBoxVisible = visible;
        SaveSettings();
        Launcher.ApplySearchBoxSettings();
    }

    public void SetSearchBoxWidth(int width)
    {
        Settings.SearchBoxWidth = Math.Clamp(width, 240, 530);
        SaveSettings();
        Launcher.ApplySearchBoxSettings();
    }

    public void SetFileSearchRoots(IEnumerable<string> roots)
    {
        Settings.FileSearchRoots = roots.ToList();
        // The list is now user-owned: empty means "index nothing", never "fall back to defaults".
        Settings.FileSearchRootsCustomized = true;
        SaveSettings();
        // No blocking walk here: the rows on screen keep answering until an idle rebuild
        // re-filters them, and the watchers are rebound to the new root list.
        MarkIndexRulesChanged();
        if (Launcher.IsOpen) Launcher.ApplyPanelStyle();
    }

    /// <summary>Entry cap for the index; takes effect on the next rebuild.</summary>
    public void SetFileSearchLimit(int limit)
    {
        Settings.FileSearchLimit = Math.Max(0, limit);
        SaveSettings();
        MarkIndexRulesChanged();
    }

    /// <summary>Per-folder caps (0 = unlimited); they take effect on the next rebuild.</summary>
    public void SetFileRootLimit(string root, int maxDepth, int maxEntries)
    {
        Settings.FileRootLimits[root] = new RootLimitSetting { MaxDepth = maxDepth, MaxEntries = maxEntries };
        SaveSettings();
        MarkIndexRulesChanged();
    }

    /// <summary>
    /// Extension allow-list for the index (".pathactive"): each entry is normalized to a
    /// lowercase dotted form; empty list = index everything.
    /// </summary>
    public void SetFileIndexExtensions(IEnumerable<string> extensions)
    {
        Settings.FileIndexExtensions = extensions
            .Select(e => e.Trim())
            .Where(e => e.Length > 1 && e.StartsWith("."))
            .Select(e => "." + e.TrimStart('.').ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SaveSettings();
        MarkIndexRulesChanged();
    }

    /// <summary>
    /// Deletes one extension everywhere: its files leave the in-memory index at once and the
    /// extension is dropped from the whitelist, so the next rebuild will not bring it back.
    /// </summary>
    public void RemoveIndexedExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return;
        Settings.FileIndexExtensions = Settings.FileIndexExtensions
            .Where(item => !string.Equals(item, extension, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveSettings();
        Files.RemoveExtension(extension);
        Files.Save();
        if (Launcher.IsOpen) Launcher.ApplyPanelStyle();
    }

    /// <summary>
    /// Folder blacklist for the index: nothing under these paths is walked or indexed.
    /// Entries may use environment variables.
    /// </summary>
    public void SetFileIndexExcludes(IEnumerable<string> excludes)
    {
        Settings.FileIndexExcludes = excludes
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SaveSettings();
        MarkIndexRulesChanged();
    }

    public void OnLauncherClosed()
    {
        if (Settings.ShowDock) Dock.HoldOpen(false);
        ScheduleIdleTrim();
    }

    // ───────────────────────── Live index refresh ─────────────────────────

    private DispatcherTimer? _indexRefreshTimer;

    /// <summary>
    /// An incremental update landed. While the panel is open and showing file results the list
    /// should follow along, but batched — a folder copy fires hundreds of changes per second.
    /// </summary>
    private void OnFileIndexChanged()
    {
        if (IsShuttingDown) return;
        if (!Dispatcher.CheckAccess())
        {
            try { Dispatcher.BeginInvoke(OnFileIndexChanged); } catch { }
            return;
        }

        if (Launcher is null || !Launcher.IsOpen) return;

        if (_indexRefreshTimer is null)
        {
            _indexRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _indexRefreshTimer.Tick += (_, _) =>
            {
                _indexRefreshTimer!.Stop();
                if (Launcher.IsOpen) Launcher.RefreshFileResults();
            };
        }
        _indexRefreshTimer.Stop();
        _indexRefreshTimer.Start();
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

    public void SetSearchDebounce(int milliseconds)
    {
        Settings.SearchDebounceMs = Math.Clamp(milliseconds, 0, 400);
        SaveSettings();
        Launcher.ApplySearchDebounce();
    }

    public void SetSearchEngines(IEnumerable<string> ids)
    {
        Settings.SearchEngines = ids.ToList();
        SaveSettings();
        if (Launcher.IsOpen) Launcher.ApplyPanelStyle();
    }

    public void SetSearchJumpRules(IEnumerable<SearchJumpRule> rules)
    {
        var configured = rules.Select(rule => rule.Copy()).ToList();
        Settings.SearchJumpRules = configured;
        Settings.SearchEngines = configured.Where(rule => rule.IsEnabled).Select(rule => rule.Id).ToList();
        SaveSettings();
        if (Launcher.IsOpen) Launcher.ApplyPanelStyle();
    }

    private void EnsureSearchJumpRules()
    {
        if (Settings.SearchJumpRules is not null) return;
        Settings.SearchJumpRules = SearchJumpRule.Defaults(Settings.SearchEngines);
        SaveSettings();
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

    /// <summary>Tray menu「重置配置」: second-level confirmation, then restore the shipped defaults.</summary>
    public void ResetSettingsWithConfirm()
    {
        var answer = System.Windows.MessageBox.Show(
            "即将把所有设置恢复为默认配置。\n\n" +
            "快捷方式与文件索引数据会保留；外观、悬浮球、布局、防抖、搜索跳转、索引目录等个性化调整会被覆盖。\n\n" +
            "确定继续吗？",
            "重置配置", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;
        ResetSettings();
    }

    /// <summary>Tray menu「重置 → 重置配置并引导」: restore the shipped defaults, then walk the
    /// user through the first-run wizard again. No extra confirm dialog — the wizard itself is
    /// the interactive step, and it re-commits FirstRunDone when it closes.</summary>
    public void ResetSettingsAndOnboard()
    {
        if (IsShuttingDown) return;
        ResetSettings();
        Settings.FirstRunDone = false;
        SaveSettings();
        try
        {
            var wizard = new OnboardingWindow();
            wizard.Show();
            wizard.Activate();
        }
        catch (Exception ex)
        {
            Log(ex);
            Settings.FirstRunDone = true;
            SaveSettings();
        }
    }

    /// <summary>
    /// Restores the shipped defaults (the code-level defaults of <see cref="AppSettings"/>).
    /// The live settings object is overwritten in place so components holding a reference to
    /// it (the floating ball) pick the new values up without being rebuilt.
    /// </summary>
    public void ResetSettings()
    {
        var defaults = new AppSettings { FirstRunDone = true };
        Settings.CopyFrom(defaults);
        SaveSettings();
        EnsureSearchJumpRules();

        ThemeService.Initialize(Settings.Theme);
        ThemeService.SetPanelOpacity(Settings.PanelOpacity);

        _hotkey?.Unregister();
        if (Settings.HotkeyEnabled && _hotkey is { } hotkey && !hotkey.Register(Settings.Hotkey))
        {
            Settings.HotkeyEnabled = false;
            SaveSettings();
        }

        Dock.ApplySettings();
        Launcher.ApplySearchDebounce();
        Launcher.ApplyLayout();
        Launcher.ApplyPanelStyle();

        // Index rules may have gone back to the defaults: mark the stored rows stale and let
        // the idle rebuild re-filter them instead of walking the disk right now.
        Files.MarkStale();
        FileSync?.RequestRebuild();

        // A settings window built against the old values would show a stale page.
        if (_settingsWindow is { IsVisible: true }) _settingsWindow.Close();

        Notify("已恢复默认配置");
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
        // Stop the delta stream before the last write, or a late batch could land mid-save.
        try { FileSync?.Dispose(); } catch { }
        try { Files.Save(); } catch { }
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
    /// `--rebuild-index`: builds the real index against the saved settings and exits. Useful for
    /// timing a first build on a large tree without opening the panel.
    /// </summary>
    private void RunRebuildIndex(string? dataDirectory = null)
    {
        try
        {
            var storage = new Storage();
            var settings = storage.LoadSettings();
            var roots = settings.FileSearchRootsCustomized ? settings.FileSearchRoots : FileIndex.DefaultRoots();
            var rules = new IndexRules(roots, settings.FileRootLimits, settings.FileIndexExtensions,
                settings.FileIndexExcludes, settings.FileSearchLimit, !settings.FileSearchRootsCustomized);

            var dataDir = dataDirectory ?? storage.Directory;
            var index = new FileIndex(dataDir);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            index.Start(rules);
            while (index.IsBuilding && watch.Elapsed < TimeSpan.FromMinutes(5)) Thread.Sleep(200);
            watch.Stop();

            var saveWatch = System.Diagnostics.Stopwatch.StartNew();
            index.Save();
            saveWatch.Stop();
            var fileSize = 0L;
            var indexPath = Path.Combine(dataDir, "index.bin");
            try { fileSize = new FileInfo(indexPath).Length; } catch { }

            var reload = new FileIndex(dataDir);
            var reloadWatch = System.Diagnostics.Stopwatch.StartNew();
            var reloaded = reload.Load(rules);
            reloadWatch.Stop();

            var report =
                $"roots ({roots.Count}):\n  " + string.Join("\n  ", roots) +
                $"\nrows: {index.Count}\nbuild: {watch.ElapsedMilliseconds} ms" +
                $"\nmemory: {index.MemoryBytes / 1024 / 1024} MB" +
                $"\nextensions: {index.GetExtensionStats().Count}" +
                $"\nsample: " + string.Join(" | ", index.Search("doc", 3).Select(entry => entry.Path)) +
                $"\nvolumes: " + string.Join(", ", index.Meta.Volumes.Select(pair => $"{pair.Key}@0x{pair.Value.NextUsn:X}")) +
                $"\nsave: {saveWatch.ElapsedMilliseconds} ms, file {fileSize / 1024} KB" +
                $"\nreload: {reloaded} in {reloadWatch.ElapsedMilliseconds} ms, rows {reload.Count}" +
                $"\ndata: {indexPath}";
            Console.WriteLine(report);
            try { File.WriteAllText(Path.Combine(dataDir, "index-build-report.txt"), report); }
            catch { }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    /// <summary>
    /// Hidden `--settings-selftest &lt;dir&gt;` mode: reports whether the inner lists of the
    /// settings pages can actually scroll, both as a routed event and as a real
    /// WM_MOUSEWHEEL. Diagnoses "the list does not scroll with the wheel".
    /// </summary>
    private async Task RunSettingsScrollSelfTestAsync(string directory)
    {
        var report = new List<string>();
        try
        {
            Directory.CreateDirectory(directory);
            var previewData = Path.Combine(directory, "_preview-data");
            Directory.CreateDirectory(previewData);
            Storage = new Storage(previewData);
            Settings = Storage.LoadSettings();
            EnsureSearchJumpRules();
            ThemeService.Initialize(AppTheme.Light);
            Files = new FileIndex(previewData);

            var window = new SettingsWindow();
            window.Show();
            window.Activate();
            // Mouse input is only reported for the active window; without this the wheel test
            // below proves nothing.
            Infrastructure.Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(window).Handle);
            await Task.Delay(600);

            foreach (var (module, host, scroller) in new[]
                     {
                         ("files", "IndexStatsHost", "IndexStatsScroller"),
                         ("excludes", "ExcludeHost", "ExcludeScroller"),
                         ("engines", "EngineHost", "EngineScroller"),
                     })
            {
                window.NavigateTo(module);
                await Task.Delay(350);
                window.UpdateLayout();

                var scroll = FindScrollViewer(window, scroller);
                var box = FindElement(window, host);
                if (scroll is null || box is null)
                {
                    report.Add($"{module}/{scroller}: NOT FOUND (scroll={scroll is not null} host={box is not null})");
                    continue;
                }

                var before = scroll.VerticalOffset;
                report.Add($"{module}/{scroller}: h={scroll.ActualHeight:0} extent={scroll.ExtentHeight:0} " +
                           $"viewport={scroll.ViewportHeight:0} scrollable={scroll.ScrollableHeight:0}");

                // Wheel over the card chrome (title, status line, padding) must scroll the list.
                if (scroll.ScrollableHeight > 0.5)
                {
                    RaiseWheel(box, -120);
                    await Task.Delay(120);
                    window.UpdateLayout();
                    report.Add($"   wheel on card chrome: {before:0} -> {scroll.VerticalOffset:0}");
                }

                // Routed event straight at the scroll viewer (does its own logic work?).
                var beforeRouted = scroll.VerticalOffset;
                RaiseWheel(scroll, -120);
                window.UpdateLayout();
                report.Add($"   routed wheel: {beforeRouted:0} -> {scroll.VerticalOffset:0}");

                // The real thing: cursor on the list, OS wheel. Windows delivers the wheel to the
                // focused window, so a window that never took focus scrolls nothing at all — which
                // is the one failure mode a synthetic event cannot show.
                var centre = scroll.PointToScreen(new Point(scroll.ActualWidth / 2, scroll.ActualHeight / 2));
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                Infrastructure.Native.GetCursorPos(out var savedCursor);
                Infrastructure.Native.SetCursorPos((int)centre.X, (int)centre.Y);
                window.Activate();
                await Task.Delay(200);
                var beforeReal = scroll.VerticalOffset;
                report.Add($"   active={window.IsActive} foregroundIsWindow=" +
                           $"{Infrastructure.Native.GetForegroundWindow() == hwnd}");
                Infrastructure.Native.mouse_event(Infrastructure.Native.MOUSEEVENTF_WHEEL, 0, 0, -120, IntPtr.Zero);
                await Task.Delay(250);
                window.UpdateLayout();
                report.Add($"   real wheel: {beforeReal:0} -> {scroll.VerticalOffset:0}");
                Infrastructure.Native.SetCursorPos(savedCursor.X, savedCursor.Y);
            }

            window.Close();
        }
        catch (Exception ex)
        {
            report.Add("error: " + ex);
        }

        foreach (var line in report) Console.WriteLine(line);
        try { File.WriteAllLines(Path.Combine(directory, "settings-scroll-selftest.txt"), report); } catch { }
        Shutdown();
    }

    /// <summary>Raises a wheel notch the way the input system does: Preview tunnel, then bubble.</summary>
    private static void RaiseWheel(UIElement target, int delta)
    {
        var args = new System.Windows.Input.MouseWheelEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, delta);
        args.RoutedEvent = UIElement.PreviewMouseWheelEvent;
        target.RaiseEvent(args);
        if (args.Handled) return;
        args.RoutedEvent = UIElement.MouseWheelEvent;
        target.RaiseEvent(args);
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root, string name)
    {
        if (root is System.Windows.Controls.ScrollViewer { } sv && sv.Name == name) return sv;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }
        return null;
    }

    private static FrameworkElement? FindElement(DependencyObject root, string name)
    {
        if (root is FrameworkElement fe && fe.Name == name) return fe;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindElement(System.Windows.Media.VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Hidden `--drag-selftest &lt;dir&gt;` mode: asserts where the borderless windows answer
    /// HTCAPTION (drag) versus HTCLIENT (the control keeps its click). Regression guard for the
    /// screen-vs-client coordinate mistake that made every button of the first-run wizard
    /// unclickable, and for Window being a Control (which made nothing draggable).
    /// </summary>
    private async Task RunDragSelfTestAsync(string directory)
    {
        var report = new List<string>();
        try
        {
            Directory.CreateDirectory(directory);
            var previewData = Path.Combine(directory, "_preview-data");
            Directory.CreateDirectory(previewData);
            Storage = new Storage(previewData);
            Settings = Storage.LoadSettings();
            EnsureSearchJumpRules();
            ThemeService.Initialize(AppTheme.Light);

            var wizard = new OnboardingWindow { Topmost = true };
            wizard.Show();
            wizard.Activate();
            await Task.Delay(450);
            report.Add($"onboarding at {wizard.Left:0},{wizard.Top:0} {wizard.ActualWidth:0}x{wizard.ActualHeight:0}");
            Probe(report, wizard, "下一步按钮", wizard.FindName("NextButton") as FrameworkElement, false);
            Probe(report, wizard, "页标题文字", wizard.FindName("HeaderTitle") as FrameworkElement, true);
            wizard.ShowPreviewStep(2); // hotkey page: the picker is a Border with its own click handler
            await Task.Delay(250);
            Probe(report, wizard, "快捷键输入框", wizard.FindName("HotkeyBox") as FrameworkElement, false);
            wizard.Close();

            var settings = new SettingsWindow { Topmost = true };
            settings.Show();
            settings.Activate();
            await Task.Delay(700);
            report.Add($"settings at {settings.Left:0},{settings.Top:0} {settings.ActualWidth:0}x{settings.ActualHeight:0}");
            var nav = (settings.FindName("SettingsNavList") as System.Windows.Controls.Panel)?.Children
                .OfType<System.Windows.Controls.Button>().FirstOrDefault();
            Probe(report, settings, "左侧分类按钮", nav, false);
            Probe(report, settings, "空白背景", settings, true, new Point(10, settings.ActualHeight / 2));
            settings.Close();
        }
        catch (Exception ex)
        {
            report.Add("error: " + ex);
        }

        foreach (var line in report) Console.WriteLine(line);
        try { File.WriteAllLines(Path.Combine(directory, "drag-selftest.txt"), report); } catch { }
        Shutdown();
    }

    private static void Probe(List<string> report, Window window, string what,
        FrameworkElement? element, bool expectDrag, Point? clientPoint = null)
    {
        if (element is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            report.Add($"  [skip] {what}: not loaded");
            return;
        }

        var client = clientPoint ?? new Point(element.ActualWidth / 2, element.ActualHeight / 2);
        var screen = element.PointToScreen(client);
        var drag = window switch
        {
            OnboardingWindow w => w.WouldDragAt(screen),
            SettingsWindow s => s.WouldDragAt(screen),
            _ => false,
        };
        report.Add($"  [{(drag == expectDrag ? "pass" : "FAIL")}] {what}: drag={drag} (expect {expectDrag})");
    }

    /// <summary>
    /// Hidden `--onboarding-preview &lt;dir&gt;` mode: renders every onboarding wizard step
    /// to PNG. Preview-only, uses an isolated data folder.
    /// </summary>
    private async Task RenderOnboardingAsync(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var previewData = Path.Combine(directory, "_preview-data");
            Directory.CreateDirectory(previewData);
            Storage = new Storage(previewData);
            Settings = Storage.LoadSettings();
            Settings.Hotkey = "Ctrl+`";
            Settings.HotkeyEnabled = true;
            Settings.FileSearchOnPlainQuery = true;
            EnsureSearchJumpRules();
            ThemeService.Initialize(AppTheme.Dark);

            var window = new Views.OnboardingWindow();
            window.Show();
            await Task.Delay(400);

            var names = new[] { "onboarding-1-welcome", "onboarding-2-drives", "onboarding-3-hotkey", "onboarding-4-search", "onboarding-5-teach" };
            for (var step = 0; step < names.Length; step++)
            {
                window.ShowPreviewStep(step);
                await Task.Delay(320);
                RenderElement(window, Path.Combine(directory, names[step] + ".png"), 1.5);
            }

            // Final step: the carousel on its last slide, so the end state (next arrow
            // disabled, last dot active) is on record too.
            window.ShowPreviewStep(4);
            window.ShowPreviewSlide(2);
            await Task.Delay(320);
            RenderElement(window, Path.Combine(directory, "onboarding-6-done.png"), 1.5);

            // The same page in the light palette: the panel artwork is a dark screenshot, so it
            // has to stay readable when the wizard itself is light.
            ThemeService.SetMode(AppTheme.Light);
            await Task.Delay(260);
            window.ShowPreviewStep(4);
            window.ShowPreviewSlide(0);
            await Task.Delay(220);
            RenderElement(window, Path.Combine(directory, "onboarding-7-light.png"), 1.5);

            window.Close();
            Shutdown();
        }
        catch (Exception ex)
        {
            Log(ex);
            Shutdown(-1);
        }
    }

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
            // Keep the preview completely away from the real %LocalAppData% data — but start
            // from a *copy* of the real shortcuts when they exist, so the screenshots show the
            // user's actual panel (icons included) without touching the running instance.
            var realData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopJumpList");
            var realShortcuts = Path.Combine(realData, "shortcuts.json");
            var previewData = Path.Combine(directory, "_preview-data");
            Directory.CreateDirectory(previewData);
            var usingRealTiles = File.Exists(realShortcuts);
            if (usingRealTiles)
                File.Copy(realShortcuts, Path.Combine(previewData, "shortcuts.json"), overwrite: true);

            Storage = new Storage(previewData);
            Settings = Storage.LoadSettings();
            EnsureSearchJumpRules();
            ThemeService.Initialize(AppTheme.Dark);
            Library = new ShortcutLibrary(Storage);
            if (usingRealTiles)
            {
                // Make sure the demo shortcut is on the panel: desktop QQ.lnk, whose icon
                // exercises the full .lnk → target exe → high-res extraction chain.
                if (File.Exists(@"C:\Users\Public\Desktop\QQ.lnk")
                    && Library.Items.All(i => !string.Equals(i.Target, @"C:\Users\Public\Desktop\QQ.lnk", StringComparison.OrdinalIgnoreCase)))
                {
                    Library.Items.Add(new ShortcutItem
                    {
                        Name = "QQ",
                        Kind = ShortcutKind.App,
                        Target = @"C:\Users\Public\Desktop\QQ.lnk"
                    });
                    Storage.SaveShortcuts(Library.Items);
                }
            }
            else
            {
                SeedPreviewItems();
            }
            SeedDemoTilesFromDisk();
            _ = IconService.LoadAsync(Library.Items);
            Apps.Start(); // preview too: the search screenshots should include app rows

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
                Launcher.Query.Text = "vscode";
                await Task.Delay(450);
                CaptureLauncher(Path.Combine(directory, $"search-fuzzy-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // The single "跳转" row, and the second level it opens.
                const System.Reflection.BindingFlags previewFlags =
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                typeof(LauncherWindow).GetMethod("OpenJumpMenu", previewFlags)?.Invoke(Launcher, new object?[] { "vscode" });
                await Task.Delay(400);
                CaptureLauncher(Path.Combine(directory, $"search-jump-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                typeof(LauncherWindow).GetMethod("CloseJumpMenu", previewFlags)?.Invoke(Launcher, null);
                await Task.Delay(250);

                // Local files: a few placeholders so the row icons and paths are visible.
                // They are only reachable through "/s"; a plain query must never show them.
                var sampleFiles = Path.Combine(directory, "_files");
                Directory.CreateDirectory(sampleFiles);
                var sampleNames = new[]
                {
                    "季度报告-2026Q4.docx", "设计稿-v3.png", "vscode-cheatsheet.md",
                    "预算表.xlsx", "会议纪要-1125.txt", "产品原型.sketch"
                };
                for (var i = 0; i < sampleNames.Length; i++)
                {
                    var p = Path.Combine(sampleFiles, sampleNames[i]);
                    File.WriteAllText(p, string.Empty);
                    // Distinct, descending timestamps so the "/s" empty state (newest first)
                    // and the in-rank recency ordering are visible in the screenshots.
                    try { File.SetLastWriteTime(p, DateTime.Now.AddMinutes(-37 * i)); } catch { }
                }
                // A nested file like the user's JDK: "/s java.exe" must put it first, and
                // "/s jdk" must find it through the folder-path match.
                var jdkBin = Path.Combine(sampleFiles, "jdk-17.0.20.1+1", "bin");
                Directory.CreateDirectory(jdkBin);
                File.WriteAllText(Path.Combine(jdkBin, "java.exe"), string.Empty);
                App.Current.Files.Start(new[] { sampleFiles }, 0, null, Settings.FileIndexExtensions);
                for (var i = 0; i < 20 && App.Current.Files.Count == 0; i++) await Task.Delay(50);
                // Wait deterministically for the app-icon warm-up on the worker thread; the
                // panel may tuck itself away during the wait, so re-open before capturing.
                for (var i = 0; i < 300 && !App.Current.Files.IconsWarm; i++) await Task.Delay(100);
                if (!Launcher.IsOpen) Launcher.Open();
                await Task.Delay(400);

                Launcher.Query.Text = "vs";
                await Task.Delay(500);
                CaptureLauncher(Path.Combine(directory, $"search-no-files-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // "/s sjg" must find 设计稿 via pinyin initials — the key new ranking tier.
                Launcher.Query.Text = "/s sjg";
                await Task.Delay(500);
                CaptureLauncher(Path.Combine(directory, $"search-pinyin-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // "/s 计算" exercises the installed-app merge (Calculator and friends).
                Launcher.Query.Text = "/s 计算";
                await Task.Delay(600);
                CaptureLauncher(Path.Combine(directory, $"search-apps-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // "/s java.exe": the exact-name hit must be the first row.
                Launcher.Query.Text = "/s java.exe";
                await Task.Delay(600);
                CaptureLauncher(Path.Combine(directory, $"search-exact-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // "/s jdk": the nested file must be found through its folder path.
                Launcher.Query.Text = "/s jdk";
                await Task.Delay(600);
                CaptureLauncher(Path.Combine(directory, $"search-path-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // Plain query with local files on: panel matches, divider, file hits.
                Settings.FileSearchOnPlainQuery = true;
                if (!Launcher.IsOpen) Launcher.Open(); // the panel can tuck itself away mid-preview
                await Task.Delay(200);
                Launcher.Query.Text = "设计";
                await Task.Delay(800);
                CaptureLauncher(Path.Combine(directory, $"search-plain-files-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // Paging: 45 files that all match "page-". Pulling the list down must keep
                // loading the next batch instead of stopping at the first page.
                var pagingDir = Path.Combine(sampleFiles, "paging");
                Directory.CreateDirectory(pagingDir);
                for (var i = 0; i < 45; i++)
                    File.WriteAllText(Path.Combine(pagingDir, $"page-{i:00}.txt"), string.Empty);
                App.Current.Files.Start(new[] { sampleFiles }, 0, null, Settings.FileIndexExtensions);
                for (var i = 0; i < 200 && App.Current.Files.IsBuilding; i++) await Task.Delay(50);
                if (!Launcher.IsOpen) Launcher.Open();
                await Task.Delay(300);
                if (!Launcher.IsOpen) Launcher.Open(); // the panel can tuck itself away mid-preview

                var total = App.Current.Files.Search("page-", 500).Count;
                Launcher.Query.Text = "page-";
                await Task.Delay(900);
                var counts = new List<int> { Launcher.Tiles.Items.Count };
                for (var pull = 0; pull < 4; pull++)
                {
                    Launcher.Scroller.ScrollToEnd();
                    await Task.Delay(500);
                    counts.Add(Launcher.Tiles.Items.Count);
                }
                File.AppendAllText(Path.Combine(directory, "paging-check.txt"),
                    $"{mode}: matches={total} rows after each pull = {string.Join(" → ", counts)}\n" +
                    $"  files={App.Current.Files.Count} building={App.Current.Files.IsBuilding} " +
                    $"enabled={Settings.FileSearchEnabled} plain={Settings.FileSearchOnPlainQuery} " +
                    $"query={typeof(LauncherWindow).GetField("_plainFileQuery", previewFlags)?.GetValue(Launcher)} " +
                    $"hasMore={typeof(LauncherWindow).GetField("_plainFileHasMore", previewFlags)?.GetValue(Launcher)} " +
                    $"loading={typeof(LauncherWindow).GetField("_plainFileLoading", previewFlags)?.GetValue(Launcher)} " +
                    $"open={Launcher.IsOpen}\n");
                CaptureLauncher(Path.Combine(directory, $"search-paged-{mode.ToString().ToLowerInvariant()}.png"), 1.5);

                // A rebuild with the same query must keep the file section (and its rows) intact.
                Launcher.ApplyPanelStyle();
                await Task.Delay(500);
                File.AppendAllText(Path.Combine(directory, "paging-check.txt"),
                    $"  after same-query rebuild = {Launcher.Tiles.Items.Count} rows\n");

                Settings.FileSearchOnPlainQuery = false;
                Launcher.Query.Text = string.Empty;

                // "/s" pages the same way (30 rows per page) and keeps the loaded pages.
                if (!Launcher.IsOpen) Launcher.Open();
                await Task.Delay(200);
                Launcher.Query.Text = "/s page-";
                await Task.Delay(900);
                var slashCounts = new List<int> { Launcher.Tiles.Items.Count };
                for (var pull = 0; pull < 3; pull++)
                {
                    Launcher.Scroller.ScrollToEnd();
                    await Task.Delay(450);
                    slashCounts.Add(Launcher.Tiles.Items.Count);
                }
                File.AppendAllText(Path.Combine(directory, "paging-check.txt"),
                    $"{mode}: /s rows after each pull = {string.Join(" → ", slashCounts)}\n");
                Launcher.Query.Text = string.Empty;

                // "/" palette: settings plus the /s hint.
                Launcher.Query.Text = "/";
                await Task.Delay(900);
                CaptureLauncher(Path.Combine(directory, $"slash-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                Launcher.Query.Text = "/s";
                await Task.Delay(420);
                CaptureLauncher(Path.Combine(directory, $"slash-files-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                Launcher.Query.Text = string.Empty;

                // Panel artwork for the first-run wizard (transparent, so it works on both themes).
                if (mode == AppTheme.Dark) await CaptureOnboardingAssets(directory);

                var settingsWindow = new SettingsWindow();
                settingsWindow.NavigateTo("appearance");
                settingsWindow.Show();
                await Task.Delay(500);
                RenderElement(settingsWindow, Path.Combine(directory, $"settings-{mode.ToString().ToLowerInvariant()}.png"), 1.4);
                DumpSettingsLayout(directory, settingsWindow);

                // Layout and hotkey pages: the two surfaces the copy trims touched most.
                settingsWindow.NavigateTo("layout");
                await Task.Delay(400);
                RenderElement(settingsWindow, Path.Combine(directory, $"settings-layout-{mode.ToString().ToLowerInvariant()}.png"), 1.4);
                settingsWindow.NavigateTo("startup");
                await Task.Delay(400);
                RenderElement(settingsWindow, Path.Combine(directory, $"settings-startup-{mode.ToString().ToLowerInvariant()}.png"), 1.4);

                // Stress the download chart's axis logic with synthetic multi-release data.
                settingsWindow.NavigateTo("about");
                settingsWindow.ShowPreviewStats();
                settingsWindow.ShowPreviewUpdateStatus("发现新版本 v9.9.9（当前 v3.14.28）");
                await Task.Delay(400);
                RenderElement(settingsWindow, Path.Combine(directory, $"settings-stats-{mode.ToString().ToLowerInvariant()}.png"), 1.4);

                settingsWindow.NavigateTo("files");
                await Task.Delay(400);
                RenderElement(settingsWindow, Path.Combine(directory, $"advanced-{mode.ToString().ToLowerInvariant()}.png"), 1.4);

                settingsWindow.NavigateTo("excludes");
                await Task.Delay(400);
                RenderElement(settingsWindow, Path.Combine(directory, $"excludes-{mode.ToString().ToLowerInvariant()}.png"), 1.4);

                // The "add an exclude path" dialog sizes itself to its content now.
                var pathDialog = new PathEntryDialog { Owner = settingsWindow };
                pathDialog.Show();
                await Task.Delay(350);
                RenderElement(pathDialog, Path.Combine(directory, $"path-dialog-{mode.ToString().ToLowerInvariant()}.png"), 1.6);
                File.WriteAllText(Path.Combine(directory, "path-dialog-size.txt"),
                    $"window={pathDialog.ActualWidth:0.##}x{pathDialog.ActualHeight:0.##} " +
                    $"content={(pathDialog.Content as FrameworkElement)?.ActualHeight:0.##}");
                pathDialog.Close();

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

    /// <summary>
    /// Tops the preview panel up with the programs and folders that actually exist on this
    /// machine, so the screenshots (and the onboarding artwork) show real icons instead of a
    /// panel with one lonely tile. Preview data only — the real shortcut list is never touched.
    /// </summary>
    private void SeedDemoTilesFromDisk()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new (string Name, ShortcutKind Kind, string Target)[]
        {
            ("微信", ShortcutKind.App, @"D:\Tencent\Weixin\Weixin.exe"),
            ("Visual Studio Code", ShortcutKind.App,
                Path.Combine(profile, @"AppData\Local\Programs\Microsoft VS Code\Code.exe")),
            ("Chrome", ShortcutKind.App, @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            ("网易云音乐", ShortcutKind.App, @"D:\CloudMusic\cloudmusic.exe"),
            ("Bandizip", ShortcutKind.App, @"C:\Program Files\Bandizip\Bandizip.exe"),
            ("下载", ShortcutKind.Folder, Path.Combine(profile, "Downloads")),
            ("桌面", ShortcutKind.Folder, Path.Combine(profile, "Desktop")),
            ("文档", ShortcutKind.Folder, Path.Combine(profile, "Documents")),
        };

        var added = 0;
        foreach (var (name, kind, target) in candidates)
        {
            // Four tiles in total (plus the 添加 slot) fill exactly one row: the panel keeps its
            // single-row shape — no half-empty second row — and the onboarding artwork stays wide.
            if (Library.Items.Count >= 4 || added >= 3) break;
            var exists = kind == ShortcutKind.Folder ? Directory.Exists(target) : File.Exists(target);
            if (!exists) continue;
            if (Library.Items.Any(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase))) continue;
            // Every slot counts: one tile per program keeps the panel readable at a glance.
            Library.Items.Add(new ShortcutItem { Name = name, Kind = kind, Target = target });
            added++;
        }

        if (added > 0) Storage.SaveShortcuts(Library.Items);
    }

    private void SeedPreviewItems()
    {
        var samples = new (string Name, ShortcutKind Kind, string Folder)[]
        {
            ("Visual Studio Code", ShortcutKind.App, null!),
            ("QQ", ShortcutKind.App, null!),
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
            // A real shortcut from the user's desktop: exercises the actual icon pipeline
            // (lnk → target exe → high-res extraction) in preview screenshots.
            if (name == "QQ") item.Target = @"C:\Users\Public\Desktop\QQ.lnk";

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

            // Drag probe: hit-test a coarse grid and report whether each point would start
            // a window drag under SettingsWindow.HitsInteractive's rules. Diagnoses
            // "window cannot be dragged" reports without an interactive desktop.
            var blocked = 0; var total = 0;
            var samples = new System.Text.StringBuilder();
            for (var y = 10; y < window.ActualHeight - 8; y += 30)
            {
                for (var x = 10; x < window.ActualWidth - 8; x += 30)
                {
                    total++;
                    var hit = window.InputHitTest(new Point(x, y)) as DependencyObject;
                    var verdict = ProbeInteractive(hit, window);
                    if (verdict) blocked++;
                    var name = Describe(hit);
                    samples.AppendLine($"{x},{y}\t{(verdict ? "BLOCK" : "drag")}\t{name}");
                }
            }
            sb.AppendLine($"drag-probe: {blocked}/{total} points block dragging");
            File.WriteAllText(Path.Combine(directory, "settings-layout.txt"), sb.ToString());
            File.WriteAllText(Path.Combine(directory, "drag-probe.txt"), samples.ToString());
        }
        catch { }
    }

    private static bool ProbeInteractive(DependencyObject? node, DependencyObject window)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, window)) return false;
            if (node is System.Windows.Controls.ScrollViewer || node is System.Windows.Controls.UserControl)
            { node = System.Windows.Media.VisualTreeHelper.GetParent(node); continue; }
            if (node is System.Windows.Controls.Control) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private static string Describe(DependencyObject? node)
    {
        var chain = new List<string>();
        for (var n = node; n is not null && chain.Count < 6; n = System.Windows.Media.VisualTreeHelper.GetParent(n))
        {
            var type = n.GetType().Name;
            if (n is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name)) type += ":" + fe.Name;
            chain.Add(type);
        }
        return string.Join(" < ", chain);
    }

    /// <summary>
    /// Panel artwork for the onboarding page: the search pill plus the tile panel, rendered with a
    /// transparent background so the wizard shows the real product instead of paragraphs of text.
    /// </summary>
    /// <summary>Height (DIPs) all carousel screenshots are padded to.</summary>
    private const double AssetHeight = 252;

    private async Task CaptureOnboardingAssets(string directory)
    {
        try
        {
            if (!Launcher.IsOpen) Launcher.Open();
            Settings.FileSearchOnPlainQuery = false;

            // The panel and pill carry drop shadows tuned for a desktop background. On the
            // wizard's light card that shadow composites into a grey sheet around the artwork,
            // so it is dropped for the capture and put back afterwards.
            var pillShadow = Launcher.Pill.Effect;
            var panelShadow = Launcher.Panel.Effect;
            Launcher.Pill.Effect = null;
            Launcher.Panel.Effect = null;

            Launcher.Query.Text = string.Empty;
            await Task.Delay(700);
            RenderTransparent(Launcher.Stack, Path.Combine(directory, "asset-panel.png"), 2, AssetHeight);

            Launcher.Query.Text = "vscode";
            await Task.Delay(800);
            RenderTransparent(Launcher.Stack, Path.Combine(directory, "asset-search.png"), 2, AssetHeight);

            Launcher.Query.Text = "/s 季度";
            await Task.Delay(900);
            RenderTransparent(Launcher.Stack, Path.Combine(directory, "asset-files.png"), 2, AssetHeight);

            Launcher.Query.Text = string.Empty;
            Launcher.Pill.Effect = pillShadow;
            Launcher.Panel.Effect = panelShadow;
        }
        catch (Exception ex) { Log(ex); }
    }

    /// <summary>
    /// Like <see cref="RenderElement"/> but keeps the element's own transparency. <paramref name="padToHeight"/>
    /// centres the element on a taller transparent canvas: all three carousel screenshots then
    /// share one aspect ratio, so the wizard can show them in a fixed-size frame without the
    /// picture jumping or floating in dead space.
    /// </summary>
    private static void RenderTransparent(FrameworkElement element, string path, double scale,
        double padToHeight = 0)
    {
        element.UpdateLayout();
        element.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var width = Math.Max(1, element.ActualWidth);
        var height = Math.Max(1, element.ActualHeight);
        var pixelWidth = Math.Max(1, (int)Math.Round(width * scale));
        var pixelHeight = Math.Max(1, (int)Math.Round(height * scale));

        var content = new RenderTargetBitmap(pixelWidth, pixelHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        content.Render(element);

        var canvasHeight = Math.Max(pixelHeight, (int)Math.Round(Math.Max(padToHeight, height) * scale));
        var canvas = new RenderTargetBitmap(pixelWidth, canvasHeight, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Coordinates here are DIPs (the bitmap is at 96*scale dpi), not pixels.
            var canvasDipHeight = canvasHeight / scale;
            dc.DrawImage(content, new Rect(0, (canvasDipHeight - height) / 2.0, width, height));
        }
        canvas.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canvas));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
