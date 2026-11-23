using System.IO;
using System.Text.Json;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

public sealed class SettingsStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public SettingsStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "settings.json");
    }

    public AppSettings Load()
    {
        if (!File.Exists(_filePath)) return new AppSettings();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, _filePath, overwrite: true);
        }
    }

    public Task SaveAsync(AppSettings settings)
    {
        return Task.Run(() => Save(settings));
    }
}
