using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.Views;

/// <summary>
/// Registry-app section of the result surface. The app index is fully independent of the
/// file index (own settings flag, no blacklist), so installed software is searchable on
/// every surface: a plain query shows it under its own divider, "/s" merges it above the
/// file hits. Shared plumbing with the file rows: icons, the deferred icon loader and the
/// one-line result model.
/// </summary>
public partial class LauncherWindow
{
    private const int AppResultLimit = 8;

    /// <summary>Rows of the current query's app section — kept between rebuilds so the list does not flicker.</summary>
    private readonly List<ResultRow> _appRows = [];
    private DividerRow? _appDivider;
    private string? _appQuery;

    /// <summary>Forgets the app section (settings toggle or a registry change).</summary>
    internal void DropAppResults()
    {
        _appQuery = null;
        _appDivider = null;
        _appRows.Clear();
    }

    /// <summary>
    /// The app rows for the active query, rebuilt only when the query text changed. Reads the
    /// in-memory registry snapshot, so this is fast enough to call on the UI thread.
    /// </summary>
    private List<ResultRow> AppRowsFor(string query)
    {
        if (!string.Equals(_appQuery, query, StringComparison.Ordinal))
        {
            _appQuery = query;
            _appRows.Clear();
            if (CurrentSettings.RegistrySearchEnabled)
            {
                // A tile the user already pinned answers first anyway; its registry twin would
                // just be a second row for the same program, so skip what the panel matched —
                // by name (「微信」 vs 「微信」) and by launch target (a tile holding Code.exe
                // versus the registry entry "Microsoft Visual Studio Code").
                var matched = ShortcutLibrary.Search(_ordered, query);
                var tileNames = matched.Select(i => i.Name.Trim())
                    .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
                var tileTargets = matched.Select(i => i.Target)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var app in App.Current.Apps.Search(query, AppResultLimit + tileNames.Count))
                {
                    if (tileNames.Contains(app.Name.Trim())) continue;
                    if (tileTargets.Contains(app.ExePath)) continue;
                    _appRows.Add(BuildAppRow(app));
                    if (_appRows.Count >= AppResultLimit) break;
                }
            }
        }
        return _appRows;
    }

    /// <summary>True when a plain query should carry the installed-app section.</summary>
    private bool AppSearchActive(string query) =>
        query.Length > 0 && !query.StartsWith('/') && CurrentSettings.RegistrySearchEnabled;

    /// <summary>
    /// Shows the app section immediately while the async file search is still running, so "/s"
    /// answers with apps first instead of flashing an empty "searching" state.
    /// </summary>
    private void ShowAppSectionSeed(string terms)
    {
        var appRows = CurrentSettings.RegistrySearchEnabled ? AppRowsFor(terms) : [];
        if (appRows.Count == 0) return;

        _appDivider ??= new DividerRow("应用");
        var rows = new List<object> { _appDivider };
        rows.AddRange(appRows);
        _lastSlashSignature = null;
        ShowSlashRows(rows, "正在搜索本地索引…");
        UpdateFileSearchChrome();
        UpdateFileSearchCount();
        QueueDeferredIconLoad(rows);
    }

    private ResultRow BuildAppRow(AppEntry app)
    {
        var where = app.Publisher.Length > 0 ? $"已安装应用 · {app.Publisher}" : "已安装应用";
        var row = new ResultRow
        {
            Item = app,
            Title = app.Name,
            Subtitle = app.Version.Length > 0 ? $"{where} · {app.Version}" : where,
            Icon = IconService.TryGetCached(app.ExePath),
            Run = () => OpenInstalledApp(app)
        };

        if (row.Icon is null && app.Name.Length > 0)
        {
            row.Glyph = app.Name[..1];
            row.Accent = Accent("#0EA5E9");
        }

        return row;
    }

    private void OpenInstalledApp(AppEntry app)
    {
        if (ShellLauncher.OpenPath(app.ExePath) is { } error) { ShowMessage(error); return; }
        Close(animated: true);
    }

    /// <summary>Right-click menu of a registry-app row: launch, locate, pin to the panel, copy path.</summary>
    private void ShowAppMenu(AppEntry app)
    {
        var menu = NewMenu();
        menu.Items.Add(Item("打开", () => OpenInstalledApp(app), "Enter"));
        menu.Items.Add(Item("打开所在位置", () => ShellLauncher.RevealPath(app.ExePath)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("添加到 Jump", () =>
        {
            if (ShellLauncher.FromInput(app.ExePath) is not { } item) return;
            Commit(new List<ShortcutItem> { item });
            ShowMessage($"已添加「{item.Name}」");
        }));
        menu.Items.Add(Item("复制路径", () =>
        {
            try { Clipboard.SetText(app.ExePath); ShowMessage("路径已复制"); }
            catch { ShowMessage("复制失败：剪贴板被占用"); }
        }));
        menu.Closed += (_, _) => { if (IsOpen && _renaming is null) Keyboard.Focus(Query); };
        menu.IsOpen = true;
    }

    /// <summary>
    /// The registry snapshot changed (install / uninstall / repair). Re-match the open query:
    /// plain queries rebuild through the normal path, "/s" reloads its result set.
    /// </summary>
    private void OnAppIndexChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsOpen) return;
            DropAppResults();
            UpdateFileSearchChrome();

            var q = CurrentQuery;
            if (q.Length == 0) return;
            if (q.StartsWith('/'))
            {
                if (TryGetFileTerms(q, out var terms)) RebuildFileSearch(terms);
            }
            else
            {
                Rebuild();
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
