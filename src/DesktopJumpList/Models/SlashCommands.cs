using System.Windows.Media;

namespace DesktopJumpList.Models;

/// <summary>
/// One line in the "/" palette: either a command you can pick (which fills the box) or a ready
/// result you can run with Enter.
/// </summary>
public sealed class SlashRow
{
    public SlashRow(string glyph, string colorHex, string title, string subtitle,
        bool isCommand = false, string? fill = null, Action? run = null)
    {
        Glyph = glyph;
        Title = title;
        Subtitle = subtitle;
        IsCommand = isCommand;
        Fill = fill;
        Run = run;

        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        brush.Freeze();
        Accent = brush;
    }

    public string Glyph { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public Brush Accent { get; }
    public bool IsCommand { get; }

    /// <summary>Command rows write this into the search box instead of running anything.</summary>
    public string? Fill { get; }

    /// <summary>Result rows execute this on Enter / click.</summary>
    public Action? Run { get; }
}

/// <summary>Static description of a slash command; the behaviour lives in the launcher.</summary>
public sealed record SlashCommand(
    string Name,
    string Title,
    string Usage,
    string Hint,
    string Glyph,
    string ColorHex,
    bool NeedsArgs = true);

/// <summary>Everything the "/" palette knows about. Search engines are added from the catalogue.</summary>
public static class SlashCommandCatalog
{
    public static IReadOnlyList<SlashCommand> All { get; } = Build();

    private static List<SlashCommand> Build()
    {
        var list = new List<SlashCommand>
        {
            new("help", "可用能力", "/help", "列出全部 / 命令", "?", "#6B7280", NeedsArgs: false),
            new("calc", "计算", "/calc 12*(8+3)", "回车把结果复制到剪贴板", "=", "#4C8BF5"),
            new("unit", "单位换算", "/unit 10cm in inch", "长度 / 重量 / 温度 / 数据 / 时间", "⇄", "#0EA5A5"),
            new("s", "搜索", "/s 关键词", "用默认搜索引擎打开浏览器", "搜", "#3B82F6"),
            new("add", "添加条目", "/add C:\\路径 或网址", "新增为快捷项", "+", "#22C55E"),
            new("pin", "固定", "/pin 名称", "把匹配的第一项固定到最前", "钉", "#F59E0B"),
            new("unpin", "取消固定", "/unpin 名称", "取消固定", "取", "#F59E0B"),
            new("del", "移除", "/del 名称", "从 Jump 中移除（可用 Ctrl+Z 撤销）", "−", "#EF4444"),
            new("theme", "外观", "/theme dark | light | auto", "切换明暗主题", "◐", "#8B5CF6"),
            new("layout", "布局", "/layout 6x4", "每行数量 × 最大行数", "▦", "#0EA5A5"),
            new("dock", "悬浮球", "/dock hide | show | size=52 | opacity=60", "显示、大小、不透明度", "●", "#10B981"),
            new("settings", "设置", "/settings", "打开设置窗口", "⚙", "#64748B", NeedsArgs: false),
            new("advanced", "高级设置", "/advanced", "搜索引擎等", "✦", "#64748B", NeedsArgs: false),
            new("data", "数据目录", "/data", "打开本机数据目录", "▤", "#64748B", NeedsArgs: false),
            new("quit", "退出", "/quit", "退出 Jump", "退", "#EF4444", NeedsArgs: false),
        };

        // One search command per engine, so "/baidu 天气" works without remembering /s.
        foreach (var engine in SearchEngineCatalog.All)
            list.Add(new SlashCommand(engine.Id, $"{engine.Name}搜索", $"/{engine.Id} 关键词",
                $"用{engine.Name}打开浏览器搜索", engine.Monogram, engine.ColorHex));

        return list;
    }

    public static SlashCommand? Find(string name) =>
        All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(c => c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase));
}
