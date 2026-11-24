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
        Settings.Columns = Math.Clamp(Settings.Columns, 3, 5);
        Settings.MaxRows = Math.Clamp(Settings.MaxRows, 2, 5);
        Settings.PeekDelayMs = Math.Clamp(Settings.PeekDelayMs, 600, 8000);
        Settings.SnapDistance = Math.Clamp(Settings.SnapDistance, 8, 200);
        if (!HotkeyService.TryParse(Settings.Hotkey, out _, out _)) Settings.Hotkey = "Ctrl+Alt+J";
        ThemeService.Initialize(Settings.Theme);
        Library = new ShortcutLibrary(Storage);

        Launcher = new LauncherWindow(Library);
        Dock = new DockWindow(Settings);
        Dock.Show();

        // The app has no taskbar button by design; the tray icon is its permanent home.
        Tray = new TrayIcon(this);

        _hotkey = new HotkeyService();
        _hotkey.Pressed += ToggleLauncher;
        if (!_hotkey.Register(Settings.Hotkey))
            Launcher.ShowNotice($"全局快捷键 {Settings.Hotkey} 注册失败，可能已被其他程序占用。可右键托盘图标打开面板。");

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

    public void ToggleLauncher()
    {
        if (IsShuttingDown || Launcher.IsModal) return;
        if (Launcher.IsOpen) Launcher.Close(animated: true);
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        if (IsShuttingDown || Launcher.IsModal) return;
        Dock.HoldOpen(true);
        Launcher.Open();
    }

    public void OpenSettings()
    {
        if (IsShuttingDown || Launcher.IsModal) return;
        ShowLauncher();
        Launcher.ShowSettings();
    }

    /// <summary>Called by the launcher once it has fully faded out.</summary>
    public void OnLauncherClosed() => Dock.HoldOpen(false);

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
        Settings.Columns = Math.Clamp(columns, 3, 5);
        SaveSettings();
        Launcher.ApplyLayout();
    }

    public void SetMaxRows(int rows)
    {
        Settings.MaxRows = Math.Clamp(rows, 2, 5);
        SaveSettings();
        Launcher.ApplyLayout();
    }

    public void SetPeekDelay(int milliseconds)
    {
        Settings.PeekDelayMs = Math.Clamp(milliseconds, 600, 8000);
        SaveSettings();
        Dock.ApplySettings();
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
        if (!_hotkey.Register(text))
        {
            _hotkey.Register(previous);
            error = $"「{text}」已被其他程序占用";
            return false;
        }

        Settings.Hotkey = text;
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

                Launcher.ShowSettings();
                await Task.Delay(500);
                CaptureLauncher(Path.Combine(directory, $"settings-{mode.ToString().ToLowerInvariant()}.png"), 1.5);
                Launcher.Close(animated: false);
            }

            DumpLayout(directory);
            Dock.PreviewSetTucked(true);
            await Task.Delay(500);
            CaptureDock(Path.Combine(directory, "dock-tucked.png"), 4);

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

    private static void RenderElement(FrameworkElement element, string path, double scale)
    {
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
