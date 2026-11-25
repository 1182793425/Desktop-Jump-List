using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DesktopJumpList.Models;

/// <summary>
/// One line in the results list (typing anything, or the "/" palette). Rows replace the tile grid
/// as soon as there is a query, because a name plus its detail reads far better on one line.
/// Implements INotifyPropertyChanged so a row can start with a letter fallback and pick up its
/// real icon a moment later, when the deferred background extraction finishes.
/// </summary>
public sealed class ResultRow : INotifyPropertyChanged
{
    private ImageSource? _icon;

    /// <summary>Tile icon for a real shortcut (may arrive after the row is shown).</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; Raise(); }
    }

    private string? _glyph;

    /// <summary>Monogram fallback, used when there is no icon (engines, commands, letters).</summary>
    public string? Glyph
    {
        get => _glyph;
        set { _glyph = value; Raise(); }
    }

    public Brush? Accent { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Palette command rows put their usage into the search box instead of running.</summary>
    public string? Fill { get; set; }

    /// <summary>Synthetic rows (search engines, commands) run this.</summary>
    public Action? Run { get; set; }

    /// <summary>The shortcut a row stands for, if any.</summary>
    public object? Item { get; set; }

    public string Tooltip => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title}\n{Subtitle}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
