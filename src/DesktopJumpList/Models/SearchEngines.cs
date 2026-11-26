using System.Windows.Media;

namespace DesktopJumpList.Models;

/// <summary>Presentation model for one configured search jump.</summary>
public sealed class SearchEngineOption
{
    private readonly SolidColorBrush _brush;

    public SearchEngineOption(string id, string name, string colorHex, string monogram, string urlTemplate)
    {
        Id = id;
        Name = name;
        ColorHex = colorHex;
        Monogram = monogram;
        UrlTemplate = urlTemplate;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        _brush = brush;
    }

    public string Id { get; }
    public string Name { get; }
    public string ColorHex { get; }
    public string Monogram { get; }
    public string UrlTemplate { get; }
    public Brush BrandBrush => _brush;

    public string BuildUrl(string query) => UrlTemplate.Contains("{input}", StringComparison.Ordinal)
        ? UrlTemplate.Replace("{input}", Uri.EscapeDataString(query), StringComparison.Ordinal)
        : UrlTemplate + Uri.EscapeDataString(query);
}

/// <summary>A user-editable named URL rule with an {input} placeholder.</summary>
public sealed class SearchJumpRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string UrlTemplate { get; set; } = "https://example.com/search?q={input}";
    public string ColorHex { get; set; } = "#64748B";
    public string Monogram { get; set; } = "搜";
    public bool IsEnabled { get; set; } = true;

    public SearchJumpRule Copy() => new()
    {
        Id = Id, Name = Name, UrlTemplate = UrlTemplate,
        ColorHex = ColorHex, Monogram = Monogram, IsEnabled = IsEnabled
    };

    public SearchEngineOption ToOption() => new(Id, Name, ColorHex, Monogram, UrlTemplate);

    public static List<SearchJumpRule> Defaults(IEnumerable<string>? enabledIds = null)
    {
        var enabled = enabledIds is null
            ? new HashSet<string>(new[] { "baidu", "google", "bing" }, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(enabledIds, StringComparer.OrdinalIgnoreCase);
        return new List<SearchJumpRule>
        {
            Make("baidu", "百度", "#2B34E8", "百", "https://www.baidu.com/s?wd={input}", enabled),
            Make("google", "谷歌", "#4285F4", "G", "https://www.google.com/search?q={input}", enabled),
            Make("bing", "必应", "#0A7C86", "b", "https://www.bing.com/search?q={input}", enabled),
            Make("zhihu", "知乎", "#056DE8", "知", "https://www.zhihu.com/search?type=content&q={input}", enabled),
            Make("taobao", "淘宝", "#FF5000", "淘", "https://s.taobao.com/search?q={input}", enabled)
        };
    }

    private static SearchJumpRule Make(string id, string name, string color, string glyph,
        string url, HashSet<string> enabled) => new()
    {
        Id = id, Name = name, ColorHex = color, Monogram = glyph,
        UrlTemplate = url, IsEnabled = enabled.Contains(id)
    };
}

public static class SearchEngineCatalog
{
    public static IEnumerable<SearchEngineOption> Resolve(IEnumerable<SearchJumpRule> rules)
    {
        foreach (var rule in rules)
            if (rule.IsEnabled && !string.IsNullOrWhiteSpace(rule.Name))
                yield return rule.ToOption();
    }
}

/// <summary>
/// The single "跳转" entry of a plain query. One row stands for every enabled search jump:
/// choosing it opens a second level where the actual target is picked, so the result list is
/// not padded with one row per engine.
/// </summary>
public sealed class JumpEntry
{
    public JumpEntry(string query, int targetCount)
    {
        Query = query;
        TargetCount = targetCount;
    }

    public string Query { get; }
    public int TargetCount { get; }
    public string Name => "跳转";
    public string Title => $"跳转「{Query}」";
    public string Detail => TargetCount > 1 ? $"{TargetCount} 个跳转目标" : "1 个跳转目标";
}
