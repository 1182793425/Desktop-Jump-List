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
            if (Items.Any(i => !i.RecentOnly && string.Equals(i.Target, c.Target, StringComparison.OrdinalIgnoreCase))) continue;

            // A real tile takes over any recent-only placeholder for the same target, so the
            // recently-used row keeps pointing at one entry instead of two.
            var placeholder = Items.FirstOrDefault(i => i.RecentOnly
                && string.Equals(i.Target, c.Target, StringComparison.OrdinalIgnoreCase));
            if (placeholder is not null)
            {
                c.OpenCount = placeholder.OpenCount;
                c.LastOpenedAt = placeholder.LastOpenedAt;
                Items.Remove(placeholder);
            }

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

    /// <summary>At most this many recent-only entries are kept; the oldest ones are dropped.</summary>
    private const int MaxRecentOnly = 40;

    /// <summary>
    /// Records a one-off launch that is not a panel tile — a file opened from the local index or
    /// a click on a search-jump row. The entry lives only in the recently-used row: it is
    /// persisted, but <see cref="Ordered"/> leaves it out of the grid.
    /// </summary>
    public ShortcutItem? RecordRecent(string name, string target, ShortcutKind kind)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;

        var item = Items.FirstOrDefault(i => i.RecentOnly
            && string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase));
        var isNew = item is null;
        if (item is null)
        {
            item = new ShortcutItem
            {
                Name = string.IsNullOrWhiteSpace(name) ? target : name,
                Target = target,
                Kind = kind,
                RecentOnly = true,
                Icon = IconService.TryGetCached(target)
            };
            Items.Add(item);
        }
        else if (!string.IsNullOrWhiteSpace(name) && !string.Equals(item.Name, name, StringComparison.Ordinal))
        {
            item.Name = name;
        }

        MarkOpened(item);
        // Trim after the timestamp lands, otherwise the brand-new entry (no LastOpenedAt yet)
        // would sort last and be the first one dropped.
        if (isNew) TrimRecents();
        // A recently used row can show the real icon once the shell has it; the tile falls back
        // to a letter until then.
        if (item.Icon is null && item.NeedsAvailabilityProbe) _ = IconService.LoadAsync(new[] { item });
        return item;
    }

    private void TrimRecents()
    {
        var stale = Items
            .Where(i => i.RecentOnly)
            .OrderByDescending(i => i.LastOpenedAt ?? DateTimeOffset.MinValue)
            .Skip(MaxRecentOnly)
            .ToList();
        foreach (var item in stale) Items.Remove(item);
    }

    /// <summary>
    /// Display order: pinned first, then the collection order. Manual order is authoritative so
    /// dragging tiles in edit mode actually reorders them; new items append at the end.
    /// Recent-only bookkeeping entries are never tiles.
    /// </summary>
    public List<ShortcutItem> Ordered()
    {
        var tiles = Items.Where(i => !i.RecentOnly);
        return tiles.Where(i => i.Pinned).Concat(tiles.Where(i => !i.Pinned)).ToList();
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

    /// <summary>
    /// Ranks matches the way a launcher is expected to behave: prefix, then word initials
    /// ("vsc" → Visual Studio Code), then substring, then a subsequence match so "vscode" still
    /// finds "Visual Studio Code". Unmatched items are dropped.
    /// </summary>
    public static List<ShortcutItem> Search(IEnumerable<ShortcutItem> ordered, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return ordered.ToList();
        var compact = TextMatch.Compact(q);

        var results = new List<(ShortcutItem item, int rank, int gap, int order)>();
        var order = 0;
        foreach (var item in ordered)
        {
            order++;
            var rank = Rank(item, q, compact, out var gap);
            if (rank >= 0) results.Add((item, rank, gap, order));
        }

        return results
            .OrderBy(r => r.rank)
            .ThenBy(r => r.gap)
            .ThenBy(r => r.order)
            .Select(r => r.item)
            .ToList();
    }

    private static int Rank(ShortcutItem item, string query, string compact, out int gap)
    {
        var rank = TextMatch.RankName(item.SearchName, item.SearchCompact, item.SearchInitials,
            item.SearchWordInitials, item.SearchPinyin, query, compact, out gap);
        if (rank >= 0) return rank;

        gap = 0;
        return item.Target.Contains(compact, StringComparison.OrdinalIgnoreCase)
            ? TextMatch.RankBelowName
            : -1;
    }
}
