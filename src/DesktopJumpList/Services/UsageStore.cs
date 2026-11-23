using System.IO;
using System.Text.Json;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public sealed class UsageStore
{
    private readonly string _filePath;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public UsageStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "usage.json");
    }

    public UsageData Load()
    {
        if (!File.Exists(_filePath)) return new UsageData();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<UsageData>(json, Options) ?? new UsageData();
        }
        catch
        {
            return new UsageData();
        }
    }

    public async Task SaveAsync(UsageData usage)
    {
        var temp = _filePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, usage, Options);
        }

        File.Move(temp, _filePath, overwrite: true);
    }
}
