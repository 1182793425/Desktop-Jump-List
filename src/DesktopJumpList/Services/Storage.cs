using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>
/// Local JSON persistence under %LocalAppData%\DesktopJumpList.
/// Writes are atomic (temp file + replace) so a crash never leaves a half-written file.
/// </summary>
public sealed class Storage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Directory { get; }
    private string ShortcutsPath => Path.Combine(Directory, "shortcuts.json");
    private string SettingsPath => Path.Combine(Directory, "settings.json");

    public Storage(string? rootDirectory = null)
    {
        Directory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopJumpList");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public List<ShortcutItem> LoadShortcuts() => Read<List<ShortcutItem>>(ShortcutsPath) ?? new();

    public void SaveShortcuts(IEnumerable<ShortcutItem> items) => Write(ShortcutsPath, items.ToList());

    public AppSettings LoadSettings() => Read<AppSettings>(SettingsPath) ?? new AppSettings();

    public void SaveSettings(AppSettings settings) => Write(SettingsPath, settings);

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch
        {
            // Corrupt file: keep a copy for the user, start clean instead of crashing.
            try { File.Copy(path, path + ".broken", overwrite: true); } catch { }
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        // File.Replace(source, dest, null) throws "path is empty" on some runtimes when the
        // backup path is null; a plain overwrite copy is reliable everywhere.
        File.Copy(temp, path, overwrite: true);
        File.Delete(temp);
    }
}
