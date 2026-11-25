using System.Globalization;
using System.Windows;
using System.Windows.Media;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.Views;

/// <summary>
/// The "/" surface. Two things live here: a tiny command list (/settings) and the "/s" mode,
/// which switches the whole result surface to local files and nothing else.
/// A plain query never reaches this file's index code — that is the whole point of /s.
/// </summary>
public partial class LauncherWindow
{
    private const string SlashSettings = "settings";
    private const int FileResultLimit = 12;

    private string? _lastSlashSignature;

    // "/f" / "/file" also surface the /s command in the palette so it is discoverable.
    private static readonly string[] FileCommandAliases =
        { "s", "f", "file", "files", "本地文件", "检索" };

    /// <summary>
    /// "/s" or "/s something" enters local-file mode. The character after the "s" must be
    /// whitespace or the end of the string, so "/settings" and "/se" stay commands instead of
    /// being swallowed by the /s prefix.
    /// </summary>
    private static bool TryParseFileQuery(string query, out string terms)
    {
        terms = string.Empty;
        var body = query.TrimStart('/');
        if (body.Length == 0 || (body[0] != 's' && body[0] != 'S')) return false;
        if (body.Length > 1 && !char.IsWhiteSpace(body[1])) return false;
        terms = body[1..].Trim();
        return true;
    }

    private void RebuildSlash(string query)
    {
        ShowSlashRows(SlashRows(query), $"没有这个命令：{query}");
    }

    /// <summary>"/s" results: only local files, no shortcuts, no web engines, no commands.</summary>
    private void RebuildFileSearch(string terms)
    {
        EnsureFileIndex();

        var rows = new List<ResultRow>();
        if (CurrentSettings.FileSearchEnabled)
        {
            var files = terms.Length == 0
                ? App.Current.Files.Take(FileResultLimit)
                : App.Current.Files.Search(terms, FileResultLimit);
            foreach (var file in files) rows.Add(BuildFileRow(file));
        }

        var index = App.Current.Files;
        var emptyText = !CurrentSettings.FileSearchEnabled
            ? "本地文件搜索已在高级设置中关闭"
            : rows.Count == 0 && index.IsBuilding
                ? "正在建立本地索引，稍后会自动刷新…"
                : terms.Length == 0
                    ? "索引为空：输入 /s 关键词检索本地文件"
                    : $"没有找到本地文件：{terms}";

        ShowSlashRows(rows, emptyText);
    }

    /// <summary>
    /// The one place that starts a disk walk. Only "/s" reaches it: opening the panel or typing
    /// a normal query never builds the index, so there is no background disk cost until asked.
    /// </summary>
    private void EnsureFileIndex()
    {
        var index = App.Current.Files;
        if (!CurrentSettings.FileSearchEnabled) return;
        if (index.IsBuilding || !index.IsStale) return;

        App.Current.StartFileIndex(() =>
        {
            // The callback arrives on the worker thread; refresh the list once the user is
            // still sitting in /s mode so the first "/s foo" is not answered with an empty list.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsOpen) return;
                var q = CurrentQuery;
                if (q.StartsWith('/') && TryParseFileQuery(q, out _)) Rebuild();
            }));
        });
    }

    /// <summary>Shared chrome for both slash surfaces: one-line rows, no recent section.</summary>
    private void ShowSlashRows(List<ResultRow> rows, string emptyText)
    {
        // ResultRows are rebuilt on every keystroke, but Sync compares by reference. Without
        // this guard an unchanged /s list would replace every container and the panel would
        // flicker on each character — the exact thing we fixed for folder navigation.
        var signature = rows.Count == 0
            ? "empty:" + emptyText
            : string.Join("\u001f", rows.Select(r => r.Title + "\u001e" + r.Subtitle));
        if (signature == _lastSlashSignature)
        {
            Tiles.SelectedIndex = rows.Count > 0 ? 0 : -1;
            return;
        }
        _lastSlashSignature = signature;

        _insertMarker = null;
        _markerIndex = -1;
        _recentItems = new List<ShortcutItem>();
        // A slash/file set is never a tile-grid set: force the next plain Rebuild() to re-sync
        // instead of short-circuiting on a stale result signature.
        _lastResultSignature = null;

        Sync(_tiles, rows.Cast<object>().ToList());
        Sync(_recentTiles, new List<object>());

        RecentSection.Visibility = Visibility.Collapsed;
        Scroller.Margin = new Thickness(14, 14, 10, 14);
        EmptyState.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;

        var none = rows.Count == 0;
        NoMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = none ? emptyText : string.Empty;
        ResultCount.Text = rows.Count > 0 ? rows.Count.ToString() : string.Empty;
        Tiles.SelectedIndex = rows.Count > 0 ? 0 : -1;

        var height = TargetPanelHeight();
        if (Math.Abs(Panel.ActualHeight - height) > 0.5)
            Panel.Set(System.Windows.Controls.Border.HeightProperty, height);
        EnsureWindowFits();
        if (Scroller.VerticalOffset > 0.5) Scroller.ScrollToTop();
    }

    private List<ResultRow> SlashRows(string query)
    {
        var body = query.TrimStart('/').Trim();
        var rows = new List<ResultRow>();

        if (body.Length == 0 || SlashSettings.StartsWith(body, StringComparison.OrdinalIgnoreCase))
        {
            rows.Add(new ResultRow
            {
                Title = "打开设置",
                Subtitle = "/settings",
                Glyph = "⚙",
                Accent = Accent("#64748B"),
                Run = () => { Close(animated: true); App.Current.OpenSettings(); }
            });
        }

        if (body.Length == 0 || FileCommandAliases.Any(a => a.StartsWith(body, StringComparison.OrdinalIgnoreCase)))
        {
            rows.Add(new ResultRow
            {
                Title = "检索本地文件",
                Subtitle = "/s 关键词 · 只显示本地文件",
                Glyph = "📁",
                Accent = Accent("#F59E0B"),
                Run = EnterFileMode
            });
        }

        return rows;
    }

    /// <summary>Choosing the /s hint just puts "/s " in the box and keeps the panel open.</summary>
    private void EnterFileMode()
    {
        Query.Text = "/s ";
        Query.CaretIndex = Query.Text.Length;
        FlushQueryDelay();
        Query.Focus();
    }

    /// <summary>A file found on disk: name plus the folder it lives in.</summary>
    private static ResultRow BuildFileRow(FileEntry file)
    {
        var where = file.IsDirectory ? $"文件夹 · {file.Folder}" : file.Folder;
        var when = RelativeTime(file.WriteTime);
        var row = new ResultRow
        {
            Item = file,
            Title = file.Name,
            Subtitle = when.Length > 0 ? $"{when} · {where}" : where,
            Icon = IconService.Get(file.Path)
        };

        if (row.Icon is null && file.Name.Length > 0)
        {
            row.Glyph = file.Name[..1];
            row.Accent = file.IsDirectory ? Accent("#F59E0B") : Accent("#64748B");
        }

        return row;
    }

    private static string RelativeTime(DateTime time)
    {
        if (time <= DateTime.MinValue) return string.Empty;
        var span = DateTime.Now - time;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} 天前";
        return time.ToString("yyyy-MM-dd");
    }

    /// <summary>Maps a shortcut / search-engine row onto the one-line list model.</summary>
    private ResultRow? ToResultRow(object item) => item switch
    {
        ShortcutItem shortcut => BuildItemRow(shortcut),
        FileEntry file => BuildFileRow(file),
        SearchResultTile search => new ResultRow
        {
            Title = search.Name,
            Subtitle = $"「{search.Query}」",
            Glyph = search.Engine.Monogram,
            Accent = search.Engine.BrandBrush,
            Run = () => OpenSearch(search)
        },
        _ => null
    };

    /// <summary>An item row shows its real icon; without one it falls back to a lettered tile.</summary>
    private static ResultRow BuildItemRow(ShortcutItem item)
    {
        var row = new ResultRow
        {
            Item = item,
            Title = item.Name,
            Subtitle = RowSubtitle(item),
            Icon = item.Icon
        };

        if (item.Icon is null && item.Name.Length > 0)
        {
            row.Glyph = item.Name[..1];
            row.Accent = NameBrushConverter.Convert(item.Name, typeof(Brush), null!, CultureInfo.InvariantCulture) as Brush;
        }

        return row;
    }

    private static readonly Infrastructure.NameToBrushConverter NameBrushConverter = new();

    private static string RowSubtitle(ShortcutItem item)
    {
        var where = item.Folder is { Length: > 0 } folder ? $"分组 · {folder}" : item.Target;
        return item.IsUnavailable ? $"目标已失效 · {where}" : where ?? string.Empty;
    }

    private static SolidColorBrush Accent(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    /// <summary>Enter or click on a result row: run it, or open the shortcut it stands for.</summary>
    private void RunResultRow(ResultRow row)
    {
        if (row.Run is { } run) { run(); return; }
        if (row.Item is { } item) { Invoke(item, null); return; }
        ShowMessage("这一行只是用法提示");
    }
}
