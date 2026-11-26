using System.Text.Json.Serialization;

namespace DesktopJumpList.Models;

public enum DockEdge
{
    /// <summary>Freely placed — keeps the last dropped position on any screen.</summary>
    None,
    Left,
    Right,
    Top,
    Bottom
}

public enum AppTheme
{
    /// <summary>Follow the Windows app theme.</summary>
    System,
    Light,
    Dark
}

/// <summary>Per-folder index caps. Either value 0 (the default) means unlimited.</summary>
public sealed class RootLimitSetting
{
    public int MaxDepth { get; set; }
    public int MaxEntries { get; set; }
}

/// <summary>Persisted to settings.json. Everything here is local-only.</summary>
public sealed class AppSettings
{
    /// <summary>Which edge the ball rests against, or None for free placement.</summary>
    public DockEdge Edge { get; set; } = DockEdge.Right;

    /// <summary>Position along the edge as a 0..1 ratio (vertical for Left/Right, horizontal for Top/Bottom).</summary>
    public double EdgeRatio { get; set; } = 0.62;

    /// <summary>Absolute screen position used while <see cref="Edge"/> is None (-1 = not set yet).</summary>
    public double FreeLeft { get; set; } = -1;
    public double FreeTop { get; set; } = -1;

    /// <summary>Idle delay before the ball tucks itself into the edge.</summary>
    public int PeekDelayMs { get; set; } = 2200;

    /// <summary>Global toggle hotkey, stored as "Ctrl+Alt+J" style text.</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+J";

    /// <summary>Global hotkey is off by default; the tray icon and floating circle are primary.</summary>
    public bool HotkeyEnabled { get; set; }

    /// <summary>Colour mode: follow system, or force light/dark. Named "themeMode" on disk so
    /// the old v2 "theme" (Light/Dark only) never collides and the default stays System.</summary>
    [JsonPropertyName("themeMode")]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Show the floating circle. Off means tray/hotkey only.</summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>Diameter of the floating circle in DIPs (32..64).</summary>
    public int DockSize { get; set; } = 48;

    /// <summary>Fill colour of the floating circle, as "#RRGGBB".</summary>
    public string DockColor { get; set; } = "#FFFFFF";

    /// <summary>Fill opacity of the floating circle (0..1).</summary>
    public double DockOpacity { get; set; } = 1.0;

    /// <summary>Corner radius of the resting circle in px; half the size gives a circle.</summary>
    public double DockRadius { get; set; } = 24;

    /// <summary>Tile icon size in DIPs (32..48).</summary>
    public int PanelIconSize { get; set; } = 40;

    /// <summary>Tile label font size in DIPs (10..14).</summary>
    public int PanelFontSize { get; set; } = 12;

    /// <summary>Space between a tile icon and its label (DIPs).</summary>
    public int PanelGap { get; set; } = 8;

    /// <summary>
    /// Space between tiles, both horizontally and vertically — the panel's CSS-gap. The panel
    /// width/height grow with it, so this is a real layout control, not just padding.
    /// </summary>
    public int PanelItemGap { get; set; } = 10;

    /// <summary>
    /// Legacy enabled search-engine IDs, retained to migrate older settings files.
    /// </summary>
    public List<string> SearchEngines { get; set; } = new() { "baidu", "google", "bing" };

    /// <summary>User-defined named URL templates offered as search jumps.</summary>
    public List<SearchJumpRule>? SearchJumpRules { get; set; }

    /// <summary>
    /// How long typing must pause before the result list is rebuilt (ms). 0 rebuilds on every
    /// keystroke; the UI echo (search box) is never delayed by this.
    /// </summary>
    public int SearchDebounceMs { get; set; } = 110;

    /// <summary>Search local files (index of the folders listed in <see cref="FileSearchRoots"/>).</summary>
    public bool FileSearchEnabled { get; set; } = true;

    /// <summary>
    /// Folders the file index walks. An empty list with
    /// <see cref="FileSearchRootsCustomized"/> = false means the defaults (Desktop /
    /// Documents / Pictures / Downloads); customized = true means "exactly this list", so the
    /// user can remove every folder and the index honours the empty list instead of silently
    /// re-adding the defaults.
    /// </summary>
    public List<string> FileSearchRoots { get; set; } = new();

    /// <summary>True once the user has added or removed a folder in advanced settings.</summary>
    public bool FileSearchRootsCustomized { get; set; }

    /// <summary>
    /// Per-folder index caps keyed by folder path. A missing entry (or a 0 value) means
    /// unlimited. Applies on the next index rebuild.
    /// </summary>
    public Dictionary<string, RootLimitSetting> FileRootLimits { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extension allow-list for the file index (the .pathactive idea): only files with these
    /// extensions are indexed. Folders are never filtered. The default covers the types a
    /// search actually wants to find — documents, images, media, archives, installers and
    /// code — and the list is freely editable in advanced settings.
    /// </summary>
    public List<string> FileIndexExtensions { get; set; } = new List<string>
    {
        // 文档
        ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".ppt", ".pptx", ".pdf",
        ".txt", ".md", ".rtf", ".csv", ".wps", ".ofd",
        // 图片
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp",
        // 视频 / 音频
        ".mp4", ".avi", ".mkv", ".mov",
        ".mp3", ".wav", ".flac", ".m4a",
        // 压缩包 / 镜像 / 安装包 / 快捷方式
        ".zip", ".rar", ".7z", ".iso",
        ".exe", ".msi", ".lnk"
    };

    /// <summary>
    /// 文件夹路径黑名单：这些路径（含子目录）下的任何文件都不索引、不遍历。
    /// 条目可使用环境变量；匹配为不区分大小写的前缀匹配。
    /// 补充了符号链接别名（All Users）、默认用户模板、安装临时目录，
    /// 以及所有盘符下的回收站和系统卷信息目录。
    /// </summary>
    public List<string> FileIndexExcludes { get; set; } = new List<string>
    {
        // 系统与程序目录
        "%SystemRoot%",
        "%ProgramFiles%",
        "%ProgramFiles(x86)%",
        "%ProgramData%",
        "%ALLUSERSPROFILE%",
        "%LOCALAPPDATA%",
        "%APPDATA%",
        "%TEMP%",

        // 用户目录下的系统别名与模板
        @"C:\Users\All Users",     // 指向 C:\ProgramData 的符号链接，之前漏网的根源
        @"C:\Users\Default",       // 系统默认用户模板

        // 系统隐藏 / 安装临时目录
        @"C:\$WinREAgent",
        @"C:\Config.Msi",
        @"C:\Recovery",
        @"C:\PerfLogs",
        @"C:\Windows.old",

        // 回收站与系统卷信息（覆盖常见盘符，按需增删）
        @"C:\$Recycle.Bin",
        @"D:\$Recycle.Bin",
        @"E:\$Recycle.Bin",
        @"C:\System Volume Information",
        @"D:\System Volume Information",
        @"E:\System Volume Information"
    };

    /// <summary>
    /// Upper bound on indexed entries; 0 (the default) means no cap. Entries are just path
    /// strings (~0.4 KB each), so even hundreds of thousands stay in the tens of MB.
    /// </summary>
    public int FileSearchLimit { get; set; } = 0;

    /// <summary>
    /// When on, a plain query (no "/s") also searches the local index: the panel's own matches
    /// stay on top, a divider follows, then the local-file hits. The index itself is still
    /// built only when the user asks for a rebuild.
    /// </summary>
    public bool FileSearchOnPlainQuery { get; set; }

    /// <summary>How many recently-used entries to keep/show (4..20).</summary>
    public int RecentCount { get; set; } = 10;

    /// <summary>Panel tint alpha (0.50..1.00). The desktop shows through as it drops.</summary>
    public double PanelOpacity { get; set; } = 0.85;

    /// <summary>Tiles per row. The panel width is derived from this, so there is no fixed limit.</summary>
    public int Columns { get; set; } = 5;

    /// <summary>Maximum visible rows before the panel scrolls. The panel height follows it (capped by the screen).</summary>
    public int MaxRows { get; set; } = 4;

    /// <summary>How close (px) a dropped ball must be to a screen edge to snap to it.</summary>
    public int SnapDistance { get; set; } = 40;

    public bool FirstRunDone { get; set; }

    public AppSettings Clone() => new()
    {
        Edge = Edge,
        EdgeRatio = EdgeRatio,
        FreeLeft = FreeLeft,
        FreeTop = FreeTop,
        PeekDelayMs = PeekDelayMs,
        Hotkey = Hotkey,
        HotkeyEnabled = HotkeyEnabled,
        Theme = Theme,
        ShowDock = ShowDock,
        DockSize = DockSize,
        DockColor = DockColor,
        DockOpacity = DockOpacity,
        DockRadius = DockRadius,
        PanelIconSize = PanelIconSize,
        PanelFontSize = PanelFontSize,
        PanelGap = PanelGap,
        PanelItemGap = PanelItemGap,
        SearchEngines = new List<string>(SearchEngines),
        SearchJumpRules = SearchJumpRules?.Select(rule => rule.Copy()).ToList(),
        SearchDebounceMs = SearchDebounceMs,
        FileSearchEnabled = FileSearchEnabled,
        FileSearchRoots = new List<string>(FileSearchRoots),
        FileSearchRootsCustomized = FileSearchRootsCustomized,
        FileRootLimits = new Dictionary<string, RootLimitSetting>(
            FileRootLimits.ToDictionary(
                kv => kv.Key,
                kv => new RootLimitSetting { MaxDepth = kv.Value.MaxDepth, MaxEntries = kv.Value.MaxEntries }),
            StringComparer.OrdinalIgnoreCase),
        FileSearchLimit = FileSearchLimit,
        FileSearchOnPlainQuery = FileSearchOnPlainQuery,
        FileIndexExtensions = new List<string>(FileIndexExtensions),
        FileIndexExcludes = new List<string>(FileIndexExcludes),
        RecentCount = RecentCount,
        PanelOpacity = PanelOpacity,
        Columns = Columns,
        MaxRows = MaxRows,
        SnapDistance = SnapDistance,
        FirstRunDone = FirstRunDone
    };
}
