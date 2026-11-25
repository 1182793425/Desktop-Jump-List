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

    /// <summary>
    /// Ranks matches the way a launcher is expected to behave: prefix, then word initials
    /// ("vsc" → Visual Studio Code), then substring, then a subsequence match so "vscode" still
    /// finds "Visual Studio Code". Unmatched items are dropped.
    /// </summary>
    public static List<ShortcutItem> Search(IEnumerable<ShortcutItem> ordered, string query)
    {
        var q = query.Trim().ToLowerInvariant();
        if (q.Length == 0) return ordered.ToList();
        var compact = q.Replace(" ", string.Empty);

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
        gap = 0;
        var name = item.SearchName;
        var initials = item.SearchInitials;
        var words = item.SearchWordInitials;

        if (name.StartsWith(query, StringComparison.Ordinal)) return 0;
        if (initials.StartsWith(compact, StringComparison.Ordinal)) return 1;
        if (words.Length > 0 && words.StartsWith(compact, StringComparison.Ordinal)) return 2;
        if (name.Contains(query, StringComparison.Ordinal)) return 3;
        if (initials.Contains(compact, StringComparison.Ordinal)) return 4;
        if (words.Length > 0 && words.Contains(compact, StringComparison.Ordinal)) return 5;

        // "vscode" is not a substring of "visual studio code", but it is a subsequence of it.
        // Single-character queries are excluded: they would match almost everything.
        if (compact.Length >= 3 && IsSubsequence(name, compact, out gap)) return 6;

        gap = 0;
        return item.Target.Contains(compact, StringComparison.OrdinalIgnoreCase) ? 7 : -1;
    }

    /// <summary>
    /// Every character of the query appears in order. The first one must sit on a word boundary,
    /// so "vscode" finds Visual Studio Code while "ode" does not pile up noise.
    /// </summary>
    private static bool IsSubsequence(string text, string query, out int gap)
    {
        gap = 0;
        var from = 0;
        var previous = -1;
        var first = true;

        foreach (var ch in query)
        {
            if (ch == ' ') continue;

            var found = text.IndexOf(ch, from);
            while (first && found > 0 && char.IsLetterOrDigit(text[found - 1]))
                found = text.IndexOf(ch, found + 1);

            if (found < 0) { gap = 0; return false; }
            if (previous >= 0) gap += found - previous - 1;
            previous = found;
            from = found + 1;
            first = false;
        }

        return true;
    }
}
