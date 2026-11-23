using System.Windows.Media;
using DesktopJumpList.Helpers;

namespace DesktopJumpList.Models;

public sealed class DayBar
{
    public DayBar(string label, int count, bool isToday, DateTime date)
    {
        Label = label;
        Count = count;
        IsToday = isToday;
        Date = date;
    }

    public string Label { get; }
    public int Count { get; }
    public bool IsToday { get; }
    public DateTime Date { get; }
}

public sealed class DonutSegment
{
    public DonutSegment(string label, int value, double percent, Brush brush)
    {
        Label = label;
        Value = value;
        Percent = percent;
        Brush = brush;
    }

    public string Label { get; }
    public int Value { get; }
    public double Percent { get; }
    public Brush Brush { get; }
    public string CountText => $"{Value} 项";
    public string PercentText => $"{Percent:0}%";
}

public sealed class TopShortcut
{
    public TopShortcut(int rank, ShortcutItem item, double ratio)
    {
        Rank = rank;
        Item = item;
        Ratio = ratio;
    }

    public int Rank { get; }
    public ShortcutItem Item { get; }
    public double Ratio { get; }
    public string RankText => Rank.ToString("00");
    public string CountText => Item.OpenCount <= 0 ? "尚未使用" : $"{Item.OpenCount} 次";
}

public sealed class GroupFilter : ObservableObject
{
    private int _count;
    private bool _isSelected;

    public GroupFilter(string name, bool isAll)
    {
        Name = name;
        IsAll = isAll;
    }

    public string Name { get; }
    public bool IsAll { get; }

    public int Count
    {
        get => _count;
        set => SetProperty(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string DisplayName => IsAll ? "全部" : Name;
}
