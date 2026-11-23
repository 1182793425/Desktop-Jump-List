using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DesktopJumpList.Infrastructure;

/// <summary>First visible character of a name, for letter-tile fallback icons.</summary>
public sealed class InitialConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = (value as string)?.Trim();
        if (string.IsNullOrEmpty(s)) return "?";
        var e = StringInfo.GetTextElementEnumerator(s);
        return e.MoveNext() ? e.GetTextElement().ToUpperInvariant() : "?";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Stable, pleasant gradient per name, so a letter tile is always the same colour.</summary>
public sealed class NameToBrushConverter : IValueConverter
{
    private static readonly (string a, string b)[] Palette =
    {
        ("#4F80FF", "#6A5CFF"), ("#FF7A59", "#FF5C8A"), ("#10B5A3", "#1D9BF0"),
        ("#F0A33A", "#FF7A59"), ("#8A5CFF", "#C15CFF"), ("#2DB36B", "#10B5A3"),
        ("#5C6BFF", "#1D9BF0"), ("#E85C7A", "#8A5CFF")
    };
    private static readonly Dictionary<int, Brush> Cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var s = value as string ?? string.Empty;
        int h = 17;
        foreach (var ch in s) h = unchecked(h * 31 + ch);
        var idx = Math.Abs(h % Palette.Length);
        if (Cache.TryGetValue(idx, out var cached)) return cached;
        var (a, b) = Palette[idx];
        var brush = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(a),
            (Color)ColorConverter.ConvertFromString(b),
            new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        Cache[idx] = brush;
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>When true: null → Visible, otherwise Collapsed.</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isNull = value is null;
        return (isNull ^ Invert) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
