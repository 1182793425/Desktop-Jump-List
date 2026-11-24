using System.IO;
using Microsoft.Win32;

namespace DesktopJumpList.Services;

/// <summary>Per-user autostart via HKCU\...\Run. No admin rights needed.</summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Jump";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string value || string.IsNullOrWhiteSpace(value)) return false;

                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe) && value.Contains(exe, StringComparison.OrdinalIgnoreCase)) return true;

                // Framework-dependent runs register "dotnet <Jump.dll>"; match the assembly too.
                var assembly = typeof(StartupService).Assembly.Location;
                return !string.IsNullOrEmpty(assembly) && value.Contains(assembly, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, CommandLine);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// The exact command Windows should run at login. `dotnet run` / framework-dependent
    /// builds hand us dotnet.exe, which would start nothing useful on its own, so the
    /// entry point assembly is passed explicitly in that case.
    /// </summary>
    private static string CommandLine
    {
        get
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return string.Empty;
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var assembly = typeof(StartupService).Assembly.Location;
                return $"\"{exe}\" \"{assembly}\"";
            }
            return $"\"{exe}\"";
        }
    }
}
