using Microsoft.Win32;

namespace DesktopJumpList.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopJumpList";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定程序路径。");
            key.SetValue(ValueName, $"\"{executable}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
