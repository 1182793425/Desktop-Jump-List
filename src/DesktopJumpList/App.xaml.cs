using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using DesktopJumpList.Models;
using Microsoft.Win32;
using DesktopJumpList.Services;
using DesktopJumpList.ViewModels;

namespace DesktopJumpList;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\DesktopJumpList.SingleInstance";
    private const string ShowEventName = @"Local\DesktopJumpList.Show";
    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private CancellationTokenSource? _listenerCancellation;

    public static string DataDirectory { get; private set; } = string.Empty;
    public static bool IsPreview { get; private set; }
    public static string? ShotPath { get; private set; }
    public static string PreviewView { get; private set; } = "shortcuts";
    public static string? ThemeOverride { get; private set; }
    public static bool NoAutoHide { get; private set; }
    public static bool IsDebug { get; private set; }

    public static MainWindow? Shell { get; private set; }

    public static void Log(string message)
    {
        if (!IsDebug) return;
        try
        {
            File.AppendAllText(Path.Combine(DataDirectory, "app.log"), $"[{DateTime.Now:HH:mm:ss.fff}] {message}\n");
        }
        catch
        {
            // Diagnostics must never break the launcher.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ParseArguments(e.Args);
        DataDirectory = IsPreview
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopJumpList", "preview")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopJumpList");
        Directory.CreateDirectory(DataDirectory);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        if (!IsPreview && !AcquireSingleInstance()) return;

        var settingsStore = new SettingsStore(DataDirectory);
        var settings = settingsStore.Load();
        if (ThemeOverride is not null &&
            Enum.TryParse<AppTheme>(ThemeOverride, ignoreCase: true, out var forcedTheme))
        {
            settings.Theme = forcedTheme;
        }

        ThemeManager.Apply(settings.Theme);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        var shortcutStore = new ShortcutStore(DataDirectory);
        var usageStore = new UsageStore(DataDirectory);
        if (IsPreview) PreviewData.EnsureSeed(shortcutStore, usageStore, force: ShotPath is not null);

        var viewModel = new MainViewModel(
            shortcutStore,
            usageStore,
            settingsStore,
            settings,
            new StartupService(),
            DataDirectory,
            IsPreview);

        Shell = new MainWindow(viewModel, settings, ShotPath, PreviewView);
        MainWindow = Shell;
        Shell.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listenerCancellation?.Cancel();
        _showEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static void ParseArguments(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--preview", StringComparison.OrdinalIgnoreCase)) IsPreview = true;
            else if (arg.StartsWith("--shot=", StringComparison.OrdinalIgnoreCase))
            {
                ShotPath = arg["--shot=".Length..].Trim('"');
                IsPreview = true;
            }
            else if (arg.StartsWith("--view=", StringComparison.OrdinalIgnoreCase))
            {
                PreviewView = arg["--view=".Length..].Trim().ToLowerInvariant();
            }
            else if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                ThemeOverride = arg["--theme=".Length..].Trim();
            }
            else if (arg.Equals("--no-autohide", StringComparison.OrdinalIgnoreCase)) NoAutoHide = true;
            else if (arg.Equals("--debug", StringComparison.OrdinalIgnoreCase)) IsDebug = true;
            else if (arg.Equals("--autostart", StringComparison.OrdinalIgnoreCase))
            {
                // Kept for the startup registry entry; no special handling needed.
            }
        }
    }

    private bool AcquireSingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                {
                    existing.Set();
                    existing.Dispose();
                }
            }
            catch
            {
                // If the running instance cannot be reached, just exit quietly.
            }

            Shutdown();
            return false;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _listenerCancellation = new CancellationTokenSource();
        _ = ListenForShowRequestsAsync(_listenerCancellation.Token);
        return true;
    }

    private async Task ListenForShowRequestsAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var signaled = await Task.Run(() =>
            {
                try
                {
                    return _showEvent?.WaitOne(500) ?? false;
                }
                catch
                {
                    return false;
                }
            }, token).ConfigureAwait(false);

            if (!signaled || token.IsCancellationRequested) continue;
            Log("single-instance show signal received");
            await Dispatcher.InvokeAsync(() => Shell?.ShowPanel());
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (ThemeManager.Mode != AppTheme.System) return;
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color)) return;
        Dispatcher.BeginInvoke(() => ThemeManager.Apply(AppTheme.System), DispatcherPriority.Background);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        TryLog(e.Exception);
        if (!IsPreview)
        {
            MessageBox.Show(
                "Jump 遇到了一点问题，已经记录到日志。你可以继续使用。\n\n" + e.Exception.Message,
                "Jump",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private static void TryLog(Exception exception)
    {
        try
        {
            var path = Path.Combine(DataDirectory, "crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{exception}\n\n");
        }
        catch
        {
            // Logging must never take down the app.
        }
    }
}
