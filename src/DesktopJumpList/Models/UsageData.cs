namespace DesktopJumpList.Models;

/// <summary>Daily launch counters keyed by local date (yyyy-MM-dd).</summary>
public sealed class UsageData
{
    public Dictionary<string, int> Daily { get; set; } = new();

    public static string Key(DateTime date) => date.ToString("yyyy-MM-dd");

    public void Record(DateTime date)
    {
        var key = Key(date);
        Daily[key] = Daily.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    public int Count(DateTime date) => Daily.TryGetValue(Key(date), out var count) ? count : 0;

    public int CountSince(DateTime start)
    {
        var total = 0;
        for (var day = start.Date; day <= DateTime.Today; day = day.AddDays(1)) total += Count(day);
        return total;
    }

    public void Clear() => Daily.Clear();
}
