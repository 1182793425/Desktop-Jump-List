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
    public double EdgeRatio { get; set; } = 0.2087;

    /// <summary>Absolute screen position used while <see cref="Edge"/> is None (-1 = not set yet).</summary>
    public double FreeLeft { get; set; } = 1661;
    public double FreeTop { get; set; } = 309;

    /// <summary>Idle delay before the ball tucks itself into the edge.</summary>
    public int PeekDelayMs { get; set; } = 200;

    /// <summary>Global toggle hotkey, stored as "Ctrl+Alt+J" style text.</summary>
    public string Hotkey { get; set; } = "Ctrl+Oem3";

    /// <summary>Global hotkey is on by default (Ctrl+` toggles the panel).</summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>Colour mode: follow system, or force light/dark. Named "themeMode" on disk so
    /// the old v2 "theme" (Light/Dark only) never collides and the default stays System.</summary>
    [JsonPropertyName("themeMode")]
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Show the floating circle. Off means tray/hotkey only.</summary>
    public bool ShowDock { get; set; } = true;

    /// <summary>Diameter of the floating circle in DIPs (32..64).</summary>
    public int DockSize { get; set; } = 45;

    /// <summary>Fill colour of the floating circle, as "#RRGGBB".</summary>
    public string DockColor { get; set; } = "#22C55E";

    /// <summary>Fill opacity of the floating circle (0..1).</summary>
    public double DockOpacity { get; set; } = 0.5;

    /// <summary>Corner radius of the resting circle in px; half the size gives a circle.</summary>
    public double DockRadius { get; set; } = 50;

    /// <summary>Tile icon size in DIPs (32..48).</summary>
    public int PanelIconSize { get; set; } = 60;

    /// <summary>Tile label font size in DIPs (10..14).</summary>
    public int PanelFontSize { get; set; } = 14;

    /// <summary>Space between a tile icon and its label (DIPs).</summary>
    public int PanelGap { get; set; } = 5;

    /// <summary>
    /// Space between tiles, both horizontally and vertically — the panel's CSS-gap. The panel
    /// width/height grow with it, so this is a real layout control, not just padding.
    /// </summary>
    public int PanelItemGap { get; set; } = 5;

    /// <summary>
    /// Legacy enabled search-engine IDs, retained to migrate older settings files. The default
    /// mirrors the shipped search-jump set below.
    /// </summary>
    public List<string> SearchEngines { get; set; } = new()
    {
        "baidu", "79e1456e713440f1b502168030a7002b", "google",
        "48e0dacf8d2744c091512e4cc592f849", "096a413ad6644a8b82398a0496def573", "taobao"
    };

    /// <summary>User-defined named URL templates offered as search jumps. The shipped default
    /// set has six engines: 百度 / 必应 / 谷歌 / 京东 / Bilibili / 淘宝.</summary>
    public List<SearchJumpRule>? SearchJumpRules { get; set; } = new()
    {
        new SearchJumpRule
        {
            Id = "baidu", Name = "百度", ColorHex = "#5100DD", Monogram = "Bai",
            UrlTemplate = "https://www.baidu.com/s?wd={input}"
        },
        new SearchJumpRule
        {
            Id = "79e1456e713440f1b502168030a7002b", Name = "必应", ColorHex = "#64748B", Monogram = "Bin",
            UrlTemplate = "https://www.bing.com/search?q={input}"
        },
        new SearchJumpRule
        {
            Id = "google", Name = "谷歌", ColorHex = "#677AE9", Monogram = "GG",
            UrlTemplate = "https://www.google.com/search?q={input}"
        },
        new SearchJumpRule
        {
            Id = "48e0dacf8d2744c091512e4cc592f849", Name = "京东", ColorHex = "#DD3626", Monogram = "JD",
            UrlTemplate = "https://search.jd.com/Search?keyword={input}"
        },
        new SearchJumpRule
        {
            Id = "096a413ad6644a8b82398a0496def573", Name = "Bilibili", ColorHex = "#FC6999", Monogram = "Bli",
            UrlTemplate = "https://search.bilibili.com/all?keyword={input}"
        },
        new SearchJumpRule
        {
            Id = "taobao", Name = "淘宝", ColorHex = "#FF5000", Monogram = "TB",
            UrlTemplate = "https://s.taobao.com/search?q={input}"
        }
    };

    /// <summary>
    /// How long typing must pause before the result list is rebuilt (ms). 0 rebuilds on every
    /// keystroke; the UI echo (search box) is never delayed by this.
    /// </summary>
    public int SearchDebounceMs { get; set; } = 100;

    /// <summary>Show the search box as soon as the panel opens (instead of only after typing).</summary>
    public bool SearchBoxVisible { get; set; } = true;

    /// <summary>Width of the search pill in DIPs; hard-capped at the panel width (530).</summary>
    public int SearchBoxWidth { get; set; } = 340;

    /// <summary>Search local files (index of the folders listed in <see cref="FileSearchRoots"/>).</summary>
    public bool FileSearchEnabled { get; set; } = true;

    /// <summary>
    /// Folders the file index walks. An empty list with
    /// <see cref="FileSearchRootsCustomized"/> = false means the defaults (Desktop /
    /// Documents / Pictures / Downloads); customized = true means "exactly this list", so the
    /// user can remove every folder and the index honours the empty list instead of silently
    /// re-adding the defaults. The shipped default indexes the C:\ and D:\ drives.
    /// </summary>
    public List<string> FileSearchRoots { get; set; } = new() { @"C:\", @"D:\" };

    /// <summary>True once the user has added or removed a folder in advanced settings.</summary>
    public bool FileSearchRootsCustomized { get; set; } = true;

    /// <summary>
    /// Per-folder index caps keyed by folder path. A missing entry (or a 0 value) means
    /// unlimited. Applies on the next index rebuild.
    /// </summary>
    public Dictionary<string, RootLimitSetting> FileRootLimits { get; set; } =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\"] = new RootLimitSetting { MaxDepth = 0, MaxEntries = 0 }
        };

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
    /// 文件夹 / 文件名黑名单：命中的文件不索引、命中的目录整棵子树都不遍历。
    /// 默认值全部写成**通配符**，不写具体盘符 —— 每个人的盘符不一样，C: 之外还可能是 D:/E:，
    /// 而系统目录、回收站、卷信息在每块盘上都存在。匹配为整条路径的通配（* 可跨目录分隔符），
    /// 所以 "*\Windows\*" 同时覆盖 C:\Windows\System32 和 D:\Windows\Old。
    /// 需要精确到某个盘时，用户自己加一条路径即可（支持 %环境变量%）。
    /// </summary>
    public List<string> FileIndexExcludes { get; set; } = new List<string>(DefaultIndexExcludes);

    /// <summary>
    /// Which generation of <see cref="DefaultIndexExcludes"/> this config has already absorbed.
    /// A config that never removed a shipped entry gets the newly shipped ones appended on load
    /// (never replacing, never resurrecting something the user deleted) — so a release that
    /// knows about one more cache directory does not require anyone to hand-edit a list.
    /// </summary>
    public int IndexExcludesVersion { get; set; }

    /// <summary>
    /// Shipped blacklist: pattern-based, so it works on any drive letter. Every entry is matched
    /// against the whole path ('*' crosses separators), and a plain folder entry matches an exact
    /// segment — "*\Temp\*" hits …\Temp\x.txt but not …\Temp Notes\x.txt.
    /// </summary>
    /// <remarks>
    /// Scope is deliberately narrow: **the system, its logs, and caches**. Nothing here is about
    /// development habits — 3.14.37 briefly shipped a much longer list (AppData, vendor,
    /// node_modules, obj, dist, target, pkg\mod, IDE folders …) and it hid files people actually
    /// searched for, so it is gone. A user who wants those can add the one line they care about.
    /// </remarks>
    public static List<string> DefaultIndexExcludes { get; } = new()
    {
        // 系统与程序目录（任何盘符）
        @"*\Windows\*",
        @"*\Program Files\*",
        @"*\Program Files (x86)\*",
        @"*\ProgramData\*",
        @"*\Users\All Users\*",
        @"*\Users\Default\*",

        // Windows 自带、用户永远用不到的那些
        @"*\$WinREAgent\*",
        @"*\$SysReset\*",
        @"*\$Windows.~*",
        @"*\$Recycle.Bin\*",
        @"*\System Volume Information\*",
        @"*\Config.Msi\*",
        @"*\Recovery\*",
        @"*\PerfLogs\*",
        @"*\Windows.old\*",
        @"*\ESD\*",
        @"*\WER\*",              // 错误报告归档

        // 系统日志
        @"*\log\*",
        @"*\logs\*",

        // 缓存：目录名里带 cache / thumb / emoji 的一律不索引（系统缩略图、聊天表情、软件缓存）
        @"*\*cache*\*",
        @"*\*thumb*\*",
        @"*\*emoji*\*",
        @"*\Temp\*",
        @"*\tmp\*",
        @"*\_temp\*"
    };

    /// <summary>
    /// Entries 3.14.37–3.14.39 shipped and 3.14.40 dropped for being too strict. Used exactly
    /// once, to shrink a config that inherited them: anything in here that is not a current
    /// default is removed, and anything the user added themselves is left alone.
    /// </summary>
    internal static readonly string[] RetiredDefaultIndexExcludes =
    {
        @"*\AppData\*", @"*\AppData\Local\Temp\*", @"*\AppData\Local\Packages\*",
        @"*\nt_temp\*",
        @"*\node_modules\*", @"*\bower_components\*", @"*\vendor\*", @"*\obj\*",
        @"*\dist\*", @"*\target\*", @"*\__pycache__\*", @"*\venv\*",
        @"*\.git\*", @"*\.svn\*", @"*\.hg\*", @"*\.vs\*", @"*\.idea\*", @"*\.vscode\*",
        @"*\.venv\*", @"*\.gradle\*", @"*\.m2\*", @"*\.nuget\*", @"*\.npm\*",
        @"*\.pnpm-store\*", @"*\.yarn\*", @"*\.cargo\*",
        @"*\pkg\mod\*", @"*\*logs*\*",
        @"*\Downloaded Installations\*"
    };

    /// <summary>Bump together with <see cref="DefaultIndexExcludes"/> so old configs catch up.</summary>
    public const int CurrentExcludesVersion = 3;

    /// <summary>
    /// Brings a stored blacklist up to the shipped one. Returns the list to store; the caller
    /// writes it back with <see cref="CurrentExcludesVersion"/>.
    /// </summary>
    /// <remarks>
    /// Two different jobs, both driven by the stored version:
    /// * below 3 the list was inflated by a release that over-reached, so the retired entries are
    ///   dropped and the current defaults filled in — this is a correction, and it is why a plain
    ///   append would not have been enough;
    /// * an empty list stays empty (that is a decision), and once a config is current nothing is
    ///   touched, so a user who deletes a default entry keeps it deleted.
    /// </remarks>
    public static List<string> UpgradeExcludes(IReadOnlyList<string> current, int version)
    {
        var list = current
            .Select(entry => entry.Trim())
            .Where(entry => entry.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The pre-wildcard list (drive-letter paths, %ENV% entries) is replaced wholesale.
        if (IsLegacyDefaultExcludes(list)) return new List<string>(DefaultIndexExcludes);

        if (list.Count == 0 || version >= CurrentExcludesVersion) return list;

        if (version < 3)
        {
            var retired = new HashSet<string>(RetiredDefaultIndexExcludes, StringComparer.OrdinalIgnoreCase);
            var defaults = new HashSet<string>(DefaultIndexExcludes, StringComparer.OrdinalIgnoreCase);
            list = list.Where(entry => !retired.Contains(entry) || defaults.Contains(entry)).ToList();
        }

        var have = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        list.AddRange(DefaultIndexExcludes.Where(entry => !have.Contains(entry)));
        return list;
    }

    /// <summary>
    /// The path-based blacklist this app shipped before the patterns above. An untouched list is
    /// upgraded silently on load; a list the user edited is left alone.
    /// </summary>
    private static readonly string[] LegacyExcludes =
    {
        "%SystemRoot%", "%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramData%", "%ALLUSERSPROFILE%",
        "%LOCALAPPDATA%", "%APPDATA%", "%TEMP%",
        @"C:\Users\All Users", @"C:\Users\Default",
        @"C:\$WinREAgent", @"C:\Config.Msi", @"C:\Recovery", @"C:\PerfLogs", @"C:\Windows.old",
        @"C:\$Recycle.Bin", @"D:\$Recycle.Bin", @"E:\$Recycle.Bin",
        @"C:\System Volume Information", @"D:\System Volume Information", @"E:\System Volume Information"
    };

    /// <summary>True when the blacklist is still exactly what older versions shipped.</summary>
    public static bool IsLegacyDefaultExcludes(IReadOnlyList<string> excludes)
    {
        var set = new HashSet<string>(excludes.Select(e => e.Trim()), StringComparer.OrdinalIgnoreCase);
        var legacy = new HashSet<string>(LegacyExcludes, StringComparer.OrdinalIgnoreCase);
        return set.SetEquals(legacy);
    }

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
    public bool FileSearchOnPlainQuery { get; set; } = true;

    /// <summary>
    /// How many characters a plain query needs before the local-file section joins in. One or two
    /// characters match almost everything and cost a full scan for nothing, so the section waits
    /// until the query is specific enough. `/s` is explicit and ignores this.
    /// </summary>
    public int FileSearchMinChars { get; set; } = 3;

    /// <summary>
    /// 注册表应用搜索：索引 Uninstall 注册表项里的已安装软件（含监听增量更新）。
    /// 默认开启，独立于文件索引——不受盘符选择与文件夹黑名单影响。
    /// </summary>
    public bool RegistrySearchEnabled { get; set; } = true;

    /// <summary>How many recently-used entries to keep/show (4..20).</summary>
    public int RecentCount { get; set; } = 10;

    /// <summary>Panel tint alpha (0.50..1.00). The desktop shows through as it drops.</summary>
    public double PanelOpacity { get; set; } = 1.0;

    /// <summary>Tiles per row. The panel width is derived from this, so there is no fixed limit.</summary>
    public int Columns { get; set; } = 5;

    /// <summary>Maximum visible rows before the panel scrolls. The panel height follows it (capped by the screen).</summary>
    public int MaxRows { get; set; } = 3;

    /// <summary>How close (px) a dropped ball must be to a screen edge to snap to it.</summary>
    public int SnapDistance { get; set; } = 100;

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
        SearchBoxVisible = SearchBoxVisible,
        SearchBoxWidth = SearchBoxWidth,
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
        FileSearchMinChars = FileSearchMinChars,
        RegistrySearchEnabled = RegistrySearchEnabled,
        FileIndexExtensions = new List<string>(FileIndexExtensions),
        FileIndexExcludes = new List<string>(FileIndexExcludes),
        IndexExcludesVersion = IndexExcludesVersion,
        RecentCount = RecentCount,
        PanelOpacity = PanelOpacity,
        Columns = Columns,
        MaxRows = MaxRows,
        SnapDistance = SnapDistance,
        FirstRunDone = FirstRunDone
    };

    /// <summary>
    /// Overwrites every field from <paramref name="source"/>. Used by「重置配置」: the live
    /// settings object is filled from a fresh default instance, so consumers holding a
    /// reference to it (the floating ball) see the reset without being rebuilt.
    /// </summary>
    public void CopyFrom(AppSettings source)
    {
        Edge = source.Edge;
        EdgeRatio = source.EdgeRatio;
        FreeLeft = source.FreeLeft;
        FreeTop = source.FreeTop;
        PeekDelayMs = source.PeekDelayMs;
        Hotkey = source.Hotkey;
        HotkeyEnabled = source.HotkeyEnabled;
        Theme = source.Theme;
        ShowDock = source.ShowDock;
        DockSize = source.DockSize;
        DockColor = source.DockColor;
        DockOpacity = source.DockOpacity;
        DockRadius = source.DockRadius;
        PanelIconSize = source.PanelIconSize;
        PanelFontSize = source.PanelFontSize;
        PanelGap = source.PanelGap;
        PanelItemGap = source.PanelItemGap;
        SearchEngines = new List<string>(source.SearchEngines);
        SearchJumpRules = source.SearchJumpRules?.Select(rule => rule.Copy()).ToList();
        SearchDebounceMs = source.SearchDebounceMs;
        SearchBoxVisible = source.SearchBoxVisible;
        SearchBoxWidth = source.SearchBoxWidth;
        FileSearchEnabled = source.FileSearchEnabled;
        FileSearchRoots = new List<string>(source.FileSearchRoots);
        FileSearchRootsCustomized = source.FileSearchRootsCustomized;
        FileRootLimits = new Dictionary<string, RootLimitSetting>(
            source.FileRootLimits.ToDictionary(
                kv => kv.Key,
                kv => new RootLimitSetting { MaxDepth = kv.Value.MaxDepth, MaxEntries = kv.Value.MaxEntries }),
            StringComparer.OrdinalIgnoreCase);
        FileSearchLimit = source.FileSearchLimit;
        FileSearchOnPlainQuery = source.FileSearchOnPlainQuery;
        FileSearchMinChars = source.FileSearchMinChars;
        RegistrySearchEnabled = source.RegistrySearchEnabled;
        FileIndexExtensions = new List<string>(source.FileIndexExtensions);
        FileIndexExcludes = new List<string>(source.FileIndexExcludes);
        IndexExcludesVersion = source.IndexExcludesVersion;
        RecentCount = source.RecentCount;
        PanelOpacity = source.PanelOpacity;
        Columns = source.Columns;
        MaxRows = source.MaxRows;
        SnapDistance = source.SnapDistance;
        FirstRunDone = source.FirstRunDone;
    }
}
