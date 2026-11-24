using System.Windows.Media;

namespace DesktopJumpList.Models;

/// <summary>
/// One web search shortcut offered while typing. The icon is drawn (brand colour + monogram)
/// rather than shipped as a bitmap, so it stays crisp at any DPI and costs no assets.
/// </summary>
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

    /// <summary>Frozen brand brush, ready to bind from XAML.</summary>
    public Brush BrandBrush => _brush;

    public string BuildUrl(string query) => UrlTemplate + Uri.EscapeDataString(query);
}

/// <summary>The engines Jump can offer. Kept in code so the list is the same everywhere.</summary>
public static class SearchEngineCatalog
{
    public static readonly IReadOnlyList<SearchEngineOption> All = new[]
    {
        new SearchEngineOption("baidu", "百度", "#2B34E8", "百", "https://www.baidu.com/s?wd="),
        new SearchEngineOption("google", "谷歌", "#4285F4", "G", "https://www.google.com/search?q="),
        new SearchEngineOption("bing", "必应", "#0A7C86", "b", "https://www.bing.com/search?q="),
        new SearchEngineOption("zhihu", "知乎", "#056DE8", "知", "https://www.zhihu.com/search?type=content&q="),
        new SearchEngineOption("taobao", "淘宝", "#FF5000", "淘", "https://s.taobao.com/search?q="),
    };

    public static SearchEngineOption? ById(string id) =>
        All.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<SearchEngineOption> Resolve(IEnumerable<string> ids)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!seen.Add(id)) continue;
            if (ById(id) is { } engine) yield return engine;
        }
    }
}

/// <summary>A "search the web for this" row appended to the result list while typing.</summary>
public sealed class SearchResultTile
{
    public SearchResultTile(SearchEngineOption engine, string query)
    {
        Engine = engine;
        Query = query;
    }

    public SearchEngineOption Engine { get; }
    public string Query { get; }
    public string Name => $"{Engine.Name}搜索";
    public string Title => $"用{Engine.Name}搜索「{Query}」";
    public string Url => Engine.BuildUrl(Query);
}
