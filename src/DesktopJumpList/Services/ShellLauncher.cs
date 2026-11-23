using System.Diagnostics;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public static class ShellLauncher
{
    public static void Open(ShortcutItem shortcut)
    {
        var info = new ProcessStartInfo
        {
            FileName = shortcut.Target,
            Arguments = shortcut.Arguments ?? string.Empty,
            UseShellExecute = true
        };
        Process.Start(info);
    }
}
