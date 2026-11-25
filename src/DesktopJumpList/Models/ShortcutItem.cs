using System.Text.Json.Serialization;
using System.Windows.Media;
using DesktopJumpList.Infrastructure;

namespace DesktopJumpList.Models;

/// <summary>
/// One launchable entry. Persisted to shortcuts.json.
/// <see cref="Icon"/> is runtime-only and filled in by the icon service.
/// </summary>
public sealed class ShortcutItem : ObservableObject
{
    private string _name = string.Empty;
    private string _target = string.Empty;
    private string? _arguments;
    private string? _folder;
    private ShortcutKind _kind = ShortcutKind.File;
    private bool _pinned;
    private int _openCount;
    private DateTimeOffset? _lastOpenedAt;
    private ImageSource? _icon;
    private bool _isUnavailable;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set
        {
            if (!Set(ref _name, value)) return;
            _searchName = null;
            _searchInitials = null;
            Raise(nameof(TooltipText));
        }
    }

    private string? _searchName;
    private string? _searchInitials;

    /// <summary>Lower-cased name, computed once — search runs on every keystroke.</summary>
    public string SearchName => _searchName ??= (_name ?? string.Empty).ToLowerInvariant();

    /// <summary>Pinyin initials, also computed once per name.</summary>
    public string SearchInitials => _searchInitials ??= Infrastructure.Pinyin.Initials(_name ?? string.Empty);

    /// <summary>Absolute path for file/folder/app, an absolute URI for <see cref="ShortcutKind.Url"/>,
    /// or the AppUserModelId for <see cref="ShortcutKind.AppX"/>.</summary>
    public string Target
    {
        get => _target;
        set { if (Set(ref _target, value)) { Raise(nameof(DisplayTarget)); Raise(nameof(TooltipText)); } }
    }

    public string? Arguments
    {
        get => _arguments;
        set => Set(ref _arguments, value);
    }

    /// <summary>Optional grouping folder name (iOS-style). Null/empty means a loose tile.</summary>
    public string? Folder
    {
        get => _folder;
        set => Set(ref _folder, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public ShortcutKind Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) { Raise(nameof(KindLabel)); Raise(nameof(TooltipText)); } }
    }

    /// <summary>Pinned entries always sort before ranked ones, protecting muscle memory.</summary>
    public bool Pinned
    {
        get => _pinned;
        set { if (Set(ref _pinned, value)) Raise(nameof(TooltipText)); }
    }

    public int OpenCount
    {
        get => _openCount;
        set => Set(ref _openCount, value);
    }

    public DateTimeOffset? LastOpenedAt
    {
        get => _lastOpenedAt;
        set => Set(ref _lastOpenedAt, value);
    }

    /// <summary>Resolved shell icon. Not serialized.</summary>
    [JsonIgnore]
    public ImageSource? Icon
    {
        get => _icon;
        set => Set(ref _icon, value);
    }

    /// <summary>True when the target path has disappeared (moved, deleted, offline drive).</summary>
    [JsonIgnore]
    public bool IsUnavailable
    {
        get => _isUnavailable;
        set { if (Set(ref _isUnavailable, value)) Raise(nameof(TooltipText)); }
    }

    /// <summary>Only file-system targets can be verified; URLs and AUMIDs cannot be probed cheaply.</summary>
    [JsonIgnore]
    public bool NeedsAvailabilityProbe => Kind is ShortcutKind.File or ShortcutKind.Folder or ShortcutKind.App;

    [JsonIgnore]
    public string KindLabel => Kind switch
    {
        ShortcutKind.Folder => "文件夹",
        ShortcutKind.App => "应用",
        ShortcutKind.AppX => "商店应用",
        ShortcutKind.Url => "网页",
        _ => "文件"
    };

    /// <summary>Compact target for tooltips: the parent folder for paths, the host for URLs.</summary>
    [JsonIgnore]
    public string DisplayTarget
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Target)) return string.Empty;
            if (Kind == ShortcutKind.Url)
            {
                return Uri.TryCreate(Target, UriKind.Absolute, out var uri) ? uri.Host : Target;
            }
            if (Kind == ShortcutKind.AppX)
            {
                // AUMID is opaque; show the app name (already in Name), so no tooltip clutter.
                return string.Empty;
            }
            return Target;
        }
    }

    /// <summary>Hover text: name, where it points, what it is, and a warning if it went missing.</summary>
    [JsonIgnore]
    public string TooltipText
    {
        get
        {
            var text = Name;
            if (!string.IsNullOrWhiteSpace(DisplayTarget)) text += "\n" + DisplayTarget;
            text += "\n" + KindLabel;
            if (Pinned) text += " · 已固定在最前";
            if (IsUnavailable) text += "\n⚠ 目标不存在，可能已被移动或删除";
            return text;
        }
    }

    /// <summary>
    /// Frequency score blended with a recency bonus. Recomputed every time the panel opens,
    /// so the things you actually reopen drift to the front without shuffling mid-session.
    /// </summary>
    public double Score(DateTimeOffset now)
    {
        double recency = 0;
        if (LastOpenedAt is { } last)
        {
            var hours = (now - last).TotalHours;
            recency = hours switch
            {
                < 1 => 10,
                < 6 => 7,
                < 24 => 5,
                < 24 * 3 => 3,
                < 24 * 14 => 1.5,
                _ => 0
            };
        }
        return OpenCount + recency;
    }

    public ShortcutItem Clone() => new()
    {
        Id = Id,
        Name = Name,
        Target = Target,
        Arguments = Arguments,
        Kind = Kind,
        Folder = Folder,
        Pinned = Pinned,
        OpenCount = OpenCount,
        LastOpenedAt = LastOpenedAt
    };
}
