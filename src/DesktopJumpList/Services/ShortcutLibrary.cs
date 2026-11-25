using System.Collections.ObjectModel;
using System.Windows.Threading;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>
/// The single source of truth for shortcuts. Saves are debounced so bursts of edits
/// (drag-dropping ten files) cost one disk write.
/// </summary>
public sealed class ShortcutLibrary
{
    private readonly Storage _storage;
    private readonly DispatcherTimer _saveTimer;

    public ObservableCollection<ShortcutItem> Items { get; }

    public ShortcutLibrary(Storage storage)
    {
        _storage = storage;
        Items = new ObservableCollection<ShortcutItem>(storage.LoadShortcuts());
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
    }

    public void SaveSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try { _storage.SaveShortcuts(Items); } catch { /* disk full / locked: try again next change */ }
    }

    /// <summary>Adds new entries, skipping targets already present. Returns what was actually added.</summary>
    public List<ShortcutItem> Add(IEnumerable<ShortcutItem> candidates)
    {
        var added = new List<ShortcutItem>();
        foreach (var c in candidates)
        {
            if (Items.Any(i => string.Equals(i.Target, c.Target, StringComparison.OrdinalIgnoreCase))) continue;
            Items.Add(c);
            added.Add(c);
        }
        if (added.Count > 0) SaveSoon();
        return added;
    }

    public int Remove(ShortcutItem item)
    {
        var index = Items.IndexOf(item);
        if (index >= 0)
        {
            Items.RemoveAt(index);
            SaveSoon();
        }
        return index;
    }

    public void Restore(ShortcutItem item, int index)
    {
        if (Items.Contains(item)) return;
        Items.Insert(Math.Clamp(index, 0, Items.Count), item);
        SaveSoon();
    }

    public void MarkOpened(ShortcutItem item)
    {
        item.OpenCount++;
        item.LastOpenedAt = DateTimeOffset.Now;
        SaveSoon();
    }

    /// <summary>
    /// Display order: pinned first, then the collection order. Manual order is authoritative so
    /// dragging tiles in edit mode actually reorders them; new items append at the end.
    /// </summary>
    public List<ShortcutItem> Ordered()
    {
        return Items.Where(i => i.Pinned).Concat(Items.Where(i => !i.Pinned)).ToList();
    }

    /// <summary>Moves an item to just before (or after) another item in the flat order.</summary>
    public void Reorder(ShortcutItem item, ShortcutItem anchor, bool after)
    {
        var from = Items.IndexOf(item);
        if (from < 0) return;
        var to = Items.IndexOf(anchor);
        if (to < 0 || ReferenceEquals(item, anchor)) return;
        if (after) to++;
        if (from < to) to--;
        to = Math.Clamp(to, 0, Items.Count - 1);
        if (from == to) return;
        Items.Move(from, to);
        SaveSoon();
    }

    /// <summary>Ranks matches: name prefix &gt; pinyin-initials prefix &gt; substring &gt; initials substring.</summary>
    public static List<ShortcutItem> Search(IEnumerable<ShortcutItem> ordered, string query)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length == 0) return ordered.ToList();

        var results = new List<(ShortcutItem item, int rank, int order)>();
        int order = 0;
        foreach (var item in ordered)
        {
            order++;
            var name = item.SearchName;
            var initials = item.SearchInitials;
            int rank;
            if (name.StartsWith(query)) rank = 0;
            else if (initials.StartsWith(query)) rank = 1;
            else if (name.Contains(query)) rank = 2;
            else if (initials.Contains(query)) rank = 3;
            else if (item.Target.Contains(query, StringComparison.OrdinalIgnoreCase)) rank = 4;
            else continue;
            results.Add((item, rank, order));
        }
        return results.OrderBy(r => r.rank).ThenBy(r => r.order).Select(r => r.item).ToList();
    }
}
