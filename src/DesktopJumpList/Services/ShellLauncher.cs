using System.Diagnostics;
using System.IO;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public static class ShellLauncher
{
    /// <summary>Opens the target via the shell. Returns an error message, or null on success.</summary>
    public static string? Open(ShortcutItem item)
    {
        try
        {
            if (item.Kind == ShortcutKind.AppX)
            {
                // Launch a Microsoft Store app by its AppUserModelId via the AppsFolder shell.
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{item.Target}") { UseShellExecute = true });
                return null;
            }

            if (item.Kind != ShortcutKind.Url && !File.Exists(item.Target) && !Directory.Exists(item.Target))
                return $"找不到：{item.Target}";

            var psi = new ProcessStartInfo(item.Target) { UseShellExecute = true };
            if (!string.IsNullOrWhiteSpace(item.Arguments)) psi.Arguments = item.Arguments;
            if (item.Kind is ShortcutKind.App or ShortcutKind.File)
            {
                var dir = Path.GetDirectoryName(item.Target);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
            }
            Process.Start(psi);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Opens a URL in the user's default browser. Returns an error message or null.</summary>
    public static string? OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Opens any path (file or folder) with the shell. Returns an error message or null.</summary>
    public static string? OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Opens Explorer with the given path selected.</summary>
    public static void RevealPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Opens Explorer with the target selected.</summary>
    public static void Reveal(ShortcutItem item)
    {
        try
        {
            if (item.Kind == ShortcutKind.Url) return;
            if (Directory.Exists(item.Target))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Target}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Target}\"") { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>Infers the kind and a friendly display name from a dropped/picked path or URL.</summary>
    public static ShortcutItem? FromInput(string input)
    {
        input = input.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(input)) return null;

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new ShortcutItem { Kind = ShortcutKind.Url, Target = uri.ToString(), Name = uri.Host.Replace("www.", "") };
        }
        if (!input.Contains("://") && input.Contains('.') && !input.Contains('\\') && !input.Contains(' ')
            && Uri.TryCreate("https://" + input, UriKind.Absolute, out var guess))
        {
            return new ShortcutItem { Kind = ShortcutKind.Url, Target = guess.ToString(), Name = guess.Host.Replace("www.", "") };
        }

        if (Directory.Exists(input))
        {
            var name = new DirectoryInfo(input).Name;
            if (string.IsNullOrEmpty(name)) name = input;
            return new ShortcutItem { Kind = ShortcutKind.Folder, Target = input, Name = name };
        }
        if (File.Exists(input))
        {
            var ext = Path.GetExtension(input).ToLowerInvariant();
            var kind = ext is ".exe" or ".lnk" or ".bat" or ".cmd" or ".appref-ms" or ".msc" ? ShortcutKind.App : ShortcutKind.File;
            var name = ext is ".lnk" or ".exe" or ".appref-ms" or ".url"
                ? Path.GetFileNameWithoutExtension(input)
                : Path.GetFileName(input);
            return new ShortcutItem { Kind = kind, Target = input, Name = name };
        }
        return null;
    }
}
