using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public static class ShellLauncher
{
    public static void Open(ShortcutItem shortcut)
    {
        var target = shortcut.Target.Trim();
        if (shortcut.Type == ShortcutType.Url)
        {
            if (!target.Contains("://", StringComparison.Ordinal)) target = "https://" + target;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return;
        }

        var expanded = Environment.ExpandEnvironmentVariables(target);
        if (!File.Exists(expanded) && !Directory.Exists(expanded))
            throw new FileNotFoundException($"找不到目标：{expanded}");

        var info = new ProcessStartInfo
        {
            FileName = expanded,
            Arguments = shortcut.Arguments ?? string.Empty,
            UseShellExecute = true,
            WorkingDirectory = Directory.Exists(expanded) ? expanded : Path.GetDirectoryName(expanded) ?? string.Empty
        };
        Process.Start(info);
    }

    public static void RevealInExplorer(ShortcutItem shortcut)
    {
        var target = Environment.ExpandEnvironmentVariables(shortcut.Target);
        if (Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
            return;
        }

        if (File.Exists(target))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
            return;
        }

        throw new FileNotFoundException($"找不到目标：{target}");
    }

    public static void OpenDataFolder(string directory)
    {
        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
    }

    public static string FriendlyError(Exception ex) => ex switch
    {
        FileNotFoundException => ex.Message,
        Win32Exception w => $"系统无法打开该目标：{w.Message}",
        _ => ex.Message
    };
}
