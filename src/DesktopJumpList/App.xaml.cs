using System.IO;
using System.Threading;
using System.Windows;
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

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
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
        ThemeService.Initialize();
        Storage = new Storage();
        Settings = Storage.LoadSettings();
        Library = new ShortcutLibrary(Storage);

        Launcher = new LauncherWindow(Library);
        Dock = new DockWindow(Settings);
        Dock.Show();

        _hotkey = new HotkeyService();
        _hotkey.Pressed += () => ToggleLauncher();
        _hotkey.Register(Settings.Hotkey);

        _wakeSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".wake");
        var wakeThread = new Thread(() =>
        {
            while (_wakeSignal.WaitOne())
                Dispatcher.BeginInvoke(() => ShowLauncher());
        }) { IsBackground = true };
        wakeThread.Start();

        _ = IconService.LoadAsync(Library.Items);

        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            SaveSettings();
            // First run: show the panel so the empty state explains how to add things.
            Dispatcher.BeginInvoke(() => ShowLauncher(), DispatcherPriority.ApplicationIdle);
        }
    }

    public void ToggleLauncher()
    {
        if (Launcher.IsOpen) Launcher.Close(animated: true);
        else ShowLauncher();
    }

    public void ShowLauncher()
    {
        Dock.HoldOpen(true);
        Launcher.Open();
    }

    /// <summary>Called by the launcher once it has fully faded out.</summary>
    public void OnLauncherClosed() => Dock.HoldOpen(false);

    public void SaveSettings()
    {
        try { Storage.SaveSettings(Settings); }
        catch (Exception ex) { Log(ex); }
    }

    public void Quit()
    {
        Library.SaveNow();
        SaveSettings();
        _hotkey?.Dispose();
        Shutdown();
    }

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
        Library?.SaveNow();
        _hotkey?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
