using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.Converters;

public sealed class IconSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ShortcutItem item ? IconService.GetIcon(item) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class NoIconVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ShortcutItem item && IconService.GetIcon(item) is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() || (value is AppTheme theme && parameter is string mode && theme.ToString() == mode);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return Binding.DoNothing;
        if (targetType == typeof(ShortcutType) && parameter is string type && Enum.TryParse<ShortcutType>(type, out var parsed))
            return parsed;
        if (targetType == typeof(AppTheme) && parameter is string mode && Enum.TryParse<AppTheme>(mode, out var theme))
            return theme;
        return Binding.DoNothing;
    }
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && !b;
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasText = value is string text && !string.IsNullOrWhiteSpace(text);
        if (Invert) hasText = !hasText;
        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BooleanToVisibilityInvertedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class ThemeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is AppTheme theme
            ? theme switch
            {
                AppTheme.System => "跟随系统",
                AppTheme.Light => "浅色",
                _ => "深色"
            }
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

public sealed class BrushOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Brush brush) return Brushes.Transparent;
        var opacity = parameter is string text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0.25;
        var copy = brush.Clone();
        copy.Opacity = opacity;
        copy.Freeze();
        return copy;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
