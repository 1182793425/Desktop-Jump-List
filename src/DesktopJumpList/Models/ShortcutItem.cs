using System.Text.Json.Serialization;
using DesktopJumpList.Helpers;

namespace DesktopJumpList.Models;

public enum ShortcutType
{
    Folder,
    File,
    Application,
    Url
}

public sealed class ShortcutItem : ObservableObject
{
    private string _name = string.Empty;
    private string _target = string.Empty;
    private ShortcutType _type;
    private string _group = "常用";
    private string? _arguments;
    private string? _iconPath;
    private int _sortOrder;
    private int _openCount;
    private DateTimeOffset? _lastOpenedAt;
    private bool _isPinned;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) OnPropertyChanged(nameof(Initial));
        }
    }

    public string Target
    {
        get => _target;
        set => SetProperty(ref _target, value);
    }

    public ShortcutType Type
    {
        get => _type;
        set
        {
            if (!SetProperty(ref _type, value)) return;
            OnPropertyChanged(nameof(TypeLabel));
            OnPropertyChanged(nameof(GlyphData));
            OnPropertyChanged(nameof(KindHint));
        }
    }

    public string Group
    {
        get => _group;
        set => SetProperty(ref _group, string.IsNullOrWhiteSpace(value) ? "常用" : value);
    }

    public string? Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string? IconPath
    {
        get => _iconPath;
        set => SetProperty(ref _iconPath, value);
    }

    public int SortOrder
    {
        get => _sortOrder;
        set => SetProperty(ref _sortOrder, value);
    }

    public int OpenCount
    {
        get => _openCount;
        set
        {
            if (!SetProperty(ref _openCount, value)) return;
            OnPropertyChanged(nameof(UsageHint));
        }
    }

    public DateTimeOffset? LastOpenedAt
    {
        get => _lastOpenedAt;
        set
        {
            if (!SetProperty(ref _lastOpenedAt, value)) return;
            OnPropertyChanged(nameof(UsageHint));
        }
    }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public bool IsPinned
    {
        get => _isPinned;
        set => SetProperty(ref _isPinned, value);
    }

    [JsonIgnore]
    public string Initial
    {
        get
        {
            var trimmed = Name?.Trim();
            return string.IsNullOrEmpty(trimmed) ? "•" : trimmed[..1].ToUpperInvariant();
        }
    }

    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        ShortcutType.Folder => "文件夹",
        ShortcutType.File => "文件",
        ShortcutType.Application => "应用",
        ShortcutType.Url => "网页",
        _ => "快捷项"
    };

    [JsonIgnore]
    public string KindHint => Type switch
    {
        ShortcutType.Folder => "文件夹",
        ShortcutType.File => "文件",
        ShortcutType.Application => "应用程序",
        ShortcutType.Url => "网页链接",
        _ => string.Empty
    };

    [JsonIgnore]
    public string GlyphData => Type switch
    {
        ShortcutType.Folder => "M3.5 7.5 A2.5 2.5 0 0 1 6 5 L9.4 5 L11.6 7.2 L18 7.2 A2.5 2.5 0 0 1 20.5 9.7 L20.5 17 A2.5 2.5 0 0 1 18 19.5 L6 19.5 A2.5 2.5 0 0 1 3.5 17 Z",
        ShortcutType.File => "M6.5 3.5 L13.5 3.5 L18 8 L18 20.5 L6.5 20.5 Z M13.5 3.5 L13.5 8 L18 8",
        ShortcutType.Application => "M4.5 5.5 L19.5 5.5 L19.5 18.5 L4.5 18.5 Z M4.5 9 L19.5 9 M7 7.3 L7.2 7.3",
        ShortcutType.Url => "M10 14 A4.2 4.2 0 0 0 16 14 L18.5 11.5 A4.2 4.2 0 0 0 12.5 5.5 L11.2 6.8 M14 10 A4.2 4.2 0 0 0 8 10 L5.5 12.5 A4.2 4.2 0 0 0 11.5 18.5 L12.8 17.2",
        _ => "M5 12 L19 12"
    };

    [JsonIgnore]
    public string UsageHint => OpenCount <= 0
        ? Group
        : LastOpenedAt is { } last
            ? $"{Group} · {Relative(last)}"
            : $"{Group} · 已用 {OpenCount} 次";

    private static string Relative(DateTimeOffset time)
    {
        var delta = DateTimeOffset.Now - time;
        if (delta.TotalMinutes < 1) return "刚刚用过";
        if (delta.TotalMinutes < 60) return $"{Math.Max(1, (int)delta.TotalMinutes)} 分钟前";
        if (delta.TotalHours < 24) return $"{Math.Max(1, (int)delta.TotalHours)} 小时前";
        if (delta.TotalDays < 7) return $"{Math.Max(1, (int)delta.TotalDays)} 天前";
        return $"{Math.Max(1, (int)delta.TotalDays)} 天前";
    }

    public ShortcutItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Target = Target,
        Type = Type,
        Group = Group,
        Arguments = Arguments,
        IconPath = IconPath,
        SortOrder = SortOrder,
        OpenCount = OpenCount,
        LastOpenedAt = LastOpenedAt,
        CreatedAt = CreatedAt,
        IsPinned = IsPinned
    };

    public void CopyFrom(ShortcutItem other)
    {
        Name = other.Name;
        Target = other.Target;
        Type = other.Type;
        Group = other.Group;
        Arguments = other.Arguments;
        IconPath = other.IconPath;
        SortOrder = other.SortOrder;
        IsPinned = other.IsPinned;
    }
}
