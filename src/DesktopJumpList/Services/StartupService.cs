using DesktopJumpList.Models;
using Microsoft.Win32;

namespace DesktopJumpList.Services;

public sealed class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopJumpList";

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("无法访问当前用户的启动项。");

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定程序路径。");
        key.SetValue(ValueName, $"\"{executable}\" --autostart");
    }
}
