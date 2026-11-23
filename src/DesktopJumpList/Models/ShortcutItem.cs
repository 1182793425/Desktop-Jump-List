namespace DesktopJumpList.Models;

public enum ShortcutType
{
    Folder,
    File,
    Application,
    Url
}

public sealed class ShortcutItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public ShortcutType Type { get; set; }
    public string Group { get; set; } = "常用";
    public string? Arguments { get; set; }
    public int SortOrder { get; set; }

    public string Glyph => Type switch
    {
        ShortcutType.Folder => "▰",
        ShortcutType.File => "▤",
        ShortcutType.Application => "◆",
        ShortcutType.Url => "↗",
        _ => "•"
    };
}
