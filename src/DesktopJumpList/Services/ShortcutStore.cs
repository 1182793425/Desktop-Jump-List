using System.Text.Json;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public sealed class ShortcutStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };

    public ShortcutStore()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopJumpList");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "shortcuts.json");
    }

    public async Task<List<ShortcutItem>> LoadAsync()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<List<ShortcutItem>>(stream, _options) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<ShortcutItem> shortcuts)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, shortcuts.OrderBy(item => item.SortOrder), _options);
    }
}
