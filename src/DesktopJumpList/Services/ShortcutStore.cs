using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public sealed class ShortcutStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public ShortcutStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "shortcuts.json");
    }

    public string FilePath => _filePath;

    public async Task<List<ShortcutItem>> LoadAsync()
    {
        if (!File.Exists(_filePath)) return [];
        try
        {
            await using var stream = File.OpenRead(_filePath);
            var items = await JsonSerializer.DeserializeAsync<List<ShortcutItem>>(stream, Options) ?? [];
            return Normalize(items);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            TryBackupCorruptFile();
            return [];
        }
    }

    private static List<ShortcutItem> Normalize(List<ShortcutItem> items)
    {
        var seen = new HashSet<int>();
        var order = 0;
        foreach (var item in items)
        {
            item.Name = item.Name?.Trim() ?? string.Empty;
            item.Target = item.Target?.Trim() ?? string.Empty;
            item.Group = string.IsNullOrWhiteSpace(item.Group) ? "常用" : item.Group.Trim();
            if (item.Id == Guid.Empty) item.Id = Guid.NewGuid();
            if (item.CreatedAt == default) item.CreatedAt = DateTimeOffset.Now;

            while (!seen.Add(item.SortOrder) && item.SortOrder != 0) item.SortOrder++;
            if (item.SortOrder == 0 && order > 0) item.SortOrder = order;
            seen.Add(item.SortOrder);
            order = Math.Max(order, item.SortOrder) + 1;
        }

        return items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) && !string.IsNullOrWhiteSpace(item.Target))
            .OrderBy(item => item.SortOrder)
            .ToList();
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            var backup = _filePath + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(_filePath, backup, overwrite: true);
        }
        catch
        {
            // A damaged file is not worth crashing the launcher over.
        }
    }

    public async Task SaveAsync(IEnumerable<ShortcutItem> shortcuts)
    {
        var temp = _filePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, shortcuts.OrderBy(item => item.SortOrder), Options);
        }

        File.Move(temp, _filePath, overwrite: true);
    }
}
