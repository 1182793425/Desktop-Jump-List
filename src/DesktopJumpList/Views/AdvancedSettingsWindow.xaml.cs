using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.Views;

/// <summary>
/// The second page of settings: currently the web-search engines that are offered while typing.
/// Opened from the settings window so the main page stays short.
/// </summary>
public partial class AdvancedSettingsWindow : Window
{
    private readonly List<(string Id, CheckBox Toggle)> _toggles = new();

    public AdvancedSettingsWindow()
    {
        InitializeComponent();
        BuildEngineRows();
        WireDebounce();
        WireFileSearch();

        SourceInitialized += (_, _) => CenterOnScreen();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        Loaded += (_, _) => Refresh();
    }

    /// <summary>The typing pause is a slider; commit when the drag/key press ends, not per pixel.</summary>
    private void WireDebounce()
    {
        DebounceSlider.Value = Math.Clamp(App.Current.Settings.SearchDebounceMs, 0, 300);
        UpdateDebounceLabel((int)DebounceSlider.Value);

        DebounceSlider.ValueChanged += (_, e) => UpdateDebounceLabel((int)Math.Round(e.NewValue));
        DebounceSlider.AddHandler(Thumb.DragCompletedEvent,
            new DragCompletedEventHandler((_, _) => CommitDebounce()));
        DebounceSlider.LostMouseCapture += (_, _) => CommitDebounce();
        DebounceSlider.KeyUp += (_, _) => CommitDebounce();
    }

    private void UpdateDebounceLabel(int milliseconds) =>
        DebounceValue.Text = milliseconds <= 0 ? "关闭" : $"{milliseconds} ms";

    private void CommitDebounce()
    {
        var milliseconds = (int)Math.Round(DebounceSlider.Value);
        if (milliseconds == App.Current.Settings.SearchDebounceMs) return;
        App.Current.SetSearchDebounce(milliseconds);
    }

    // ───────────────────────── local file search ─────────────────────────

    private System.Windows.Threading.DispatcherTimer? _statusTimer;

    private void WireFileSearch()
    {
        FileSearchSwitch.IsChecked = App.Current.Settings.FileSearchEnabled;
        FileLimitBox.Text = App.Current.Settings.FileSearchLimit.ToString();
        FileLimitBox.LostFocus += (_, _) => CommitFileLimit();
        FileLimitBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitFileLimit(); };
        BuildRootRows();
        UpdateFileStatus();

        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statusTimer.Tick += (_, _) => UpdateFileStatus();
        _statusTimer.Start();
        Closed += (_, _) => _statusTimer?.Stop();
    }

    /// <summary>0 = no cap; anything else applies on the next rebuild.</summary>
    private void CommitFileLimit()
    {
        if (!int.TryParse(FileLimitBox.Text.Trim(), out var value) || value < 0) value = 0;
        FileLimitBox.Text = value.ToString();
        if (value == App.Current.Settings.FileSearchLimit) return;
        App.Current.SetFileSearchLimit(value);
        UpdateFileStatus();
    }

    /// <summary>
    /// The folders actually walked: the user's list once customized (empty = index nothing),
    /// the defaults otherwise. Showing the defaults is a preview only — the list becomes
    /// user-owned the first time a folder is added or removed.
    /// </summary>
    private List<string> EffectiveRoots() =>
        App.Current.Settings.FileSearchRootsCustomized
            ? App.Current.Settings.FileSearchRoots.ToList()
            : FileIndex.DefaultRoots();

    private void BuildRootRows()
    {
        RootRows.Children.Clear();

        // Column header so the two numeric boxes are self-explanatory.
        var header = new Grid { Margin = new Thickness(2, 3, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        foreach (var (text, column, align) in new[]
                 {
                     ("索引目录", 0, HorizontalAlignment.Left),
                     ("层", 1, HorizontalAlignment.Center),
                     ("条", 2, HorizontalAlignment.Center),
                     ("", 3, HorizontalAlignment.Left)
                 })
        {
            var h = new TextBlock
            {
                Text = text,
                Style = (Style)Resources["Hint"],
                FontSize = 10.5,
                TextAlignment = align == HorizontalAlignment.Center ? TextAlignment.Center : TextAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(h, column);
            header.Children.Add(h);
        }
        RootRows.Children.Add(header);

        foreach (var root in EffectiveRoots())
        {
            var limits = App.Current.Settings.FileRootLimits.TryGetValue(root, out var l) ? l : new RootLimitSetting();

            var row = new Grid { Margin = new Thickness(2, 3, 0, 3), Tag = root };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = root,
                Style = (Style)Resources["Hint"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = root
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var depthBox = MakeLimitBox(limits.MaxDepth, "最大层数，0 = 不限制");
            var entryBox = MakeLimitBox(limits.MaxEntries, "最大索引条数，0 = 不限制");
            Grid.SetColumn(depthBox, 1);
            Grid.SetColumn(entryBox, 2);
            row.Children.Add(depthBox);
            row.Children.Add(entryBox);

            var remove = new Button
            {
                Content = "移除",
                Style = (Style)Resources["LinkButton"],
                Padding = new Thickness(0, 2, 0, 2),
                FontSize = 11,
                Tag = root
            };
            remove.Click += OnRemoveRoot;
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);

            RootRows.Children.Add(row);
        }

        if (EffectiveRoots().Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "索引目录为空：/s 仍可搜到已安装应用，但不会有文件结果。点「＋ 添加文件夹…」恢复。",
                Style = (Style)Resources["Hint"],
                Margin = new Thickness(2, 3, 0, 3),
                TextWrapping = TextWrapping.Wrap
            };
            RootRows.Children.Add(hint);
        }
    }

    /// <summary>Small numeric box; commits on focus loss or Enter, 0 = unlimited.</summary>
    private TextBox MakeLimitBox(int value, string tooltip)
    {
        var box = new TextBox
        {
            Text = value.ToString(),
            FontSize = 11,
            Padding = new Thickness(2, 2, 2, 2),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("HoverBrush"),
            Foreground = (Brush)FindResource("InkBrush"),
            BorderBrush = (Brush)FindResource("CardBorderBrush"),
            ToolTip = tooltip + "（改动在下次重建索引时生效）"
        };
        box.LostFocus += (_, _) => CommitRootLimits(box);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRootLimits(box); };
        return box;
    }

    /// <summary>Commits both caps of the row the edited box belongs to.</summary>
    private void CommitRootLimits(TextBox edited)
    {
        if (edited.Parent is not Grid { Tag: string root }) return;
        var boxes = edited.Parent is Grid g
            ? g.Children.OfType<TextBox>().Take(2).ToList()
            : new List<TextBox>();

        int Parse(TextBox? box)
        {
            if (box is null || !int.TryParse(box.Text.Trim(), out var v) || v < 0) return 0;
            box.Text = v.ToString();
            return v;
        }

        App.Current.SetFileRootLimit(root, Parse(boxes.ElementAtOrDefault(0)), Parse(boxes.ElementAtOrDefault(1)));
        UpdateFileStatus();
    }

    private void OnFileSearchToggled(object sender, RoutedEventArgs e)
    {
        App.Current.SetFileSearchEnabled(FileSearchSwitch.IsChecked == true);
        UpdateFileStatus();
    }

    private void OnRemoveRoot(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string root }) return;
        var roots = EffectiveRoots();
        roots.RemoveAll(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase));
        App.Current.SetFileSearchRoots(roots);
        BuildRootRows();
        UpdateFileStatus();
    }

    private void OnAddRoot(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择要加入索引的文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (string.IsNullOrWhiteSpace(dialog.SelectedPath)) return;

        var roots = EffectiveRoots();
        if (roots.Any(r => string.Equals(r, dialog.SelectedPath, StringComparison.OrdinalIgnoreCase))) return;
        roots.Add(dialog.SelectedPath);
        App.Current.SetFileSearchRoots(roots);
        BuildRootRows();
        UpdateFileStatus();
    }

    private void UpdateFileStatus()
    {
        if (!App.Current.Settings.FileSearchEnabled)
        {
            FileIndexStatus.Text = "已关闭：输入 /s 也不会检索本地文件";
            AddRootButton.IsEnabled = false;
            return;
        }

        AddRootButton.IsEnabled = true;
        var index = App.Current.Files;
        var cap = App.Current.Settings.FileSearchLimit > 0
            ? $" · 总上限 {App.Current.Settings.FileSearchLimit:N0} 条"
            : "";
        var memory = FormatBytes(index.MemoryBytes);
        FileIndexStatus.Text = index.IsBuilding
            ? $"正在索引…（已有 {index.Count} 项，缓存约 {memory}）"
            : EffectiveRoots().Count == 0
                ? "索引目录为空：/s 仍可搜到已安装应用"
                : index.Count == 0
                    ? "尚未建立索引，输入 /s 时才会开始" + cap
                    : $"已索引 {index.Count} 项 · 缓存约 {memory} · 更新于 {index.LastBuilt:HH:mm}" + cap;
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB"
    };

    private void CenterOnScreen()
    {
        var area = ScreenGeometry.WorkAreaAtCursor(this);
        var max = Math.Max(360, area.Height - 24);
        if (Height > max) Height = max;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void BuildEngineRows()
    {
        foreach (var engine in SearchEngineCatalog.All)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), MinHeight = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Background = engine.BrandBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = engine.Monogram,
                Foreground = Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(badge);

            var name = new TextBlock
            {
                Text = engine.Name,
                Style = (Style)Resources["RowLabel"],
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var toggle = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = engine.Id
            };
            toggle.Click += OnEngineToggled;
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);

            _toggles.Add((engine.Id, toggle));
            EngineRows.Children.Add(row);
        }
    }

    private void Refresh()
    {
        var enabled = App.Current.Settings.SearchEngines;
        foreach (var (id, toggle) in _toggles)
            toggle.IsChecked = enabled.Any(e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase));
    }

    private void OnEngineToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } clicked) return;

        // Keep the catalogue order so the result rows never reshuffle.
        var current = new HashSet<string>(App.Current.Settings.SearchEngines, StringComparer.OrdinalIgnoreCase);
        if (clicked.IsChecked == true) current.Add(id);
        else current.Remove(id);

        var ordered = SearchEngineCatalog.All.Where(x => current.Contains(x.Id)).Select(x => x.Id).ToList();
        App.Current.SetSearchEngines(ordered);
        Refresh();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        App.Current.SetSearchEngines(new[] { "baidu", "google", "bing" });
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
