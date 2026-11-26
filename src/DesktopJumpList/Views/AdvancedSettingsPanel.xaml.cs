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
/// Advanced modules embedded in the settings window's right detail pane.
/// </summary>
public partial class AdvancedSettingsPanel : UserControl
{
    private Window? _owner;

    public AdvancedSettingsPanel()
    {
        InitializeComponent();
        BuildEngineRows();
        WireDebounce();
        WireFileSearch();
        Loaded += (_, _) =>
        {
            if (_owner is null && Window.GetWindow(this) is { } owner)
            {
                _owner = owner;
                owner.Closed += (_, _) => _statusTimer?.Stop();
            }
        };
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
        FileExtBox.Text = string.Join("\n", App.Current.Settings.FileIndexExtensions);
        FileExtBox.LostFocus += (_, _) => CommitFileExtensions();
        FileExtBox.KeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) CommitFileExtensions(); };
        BuildRootRows();
        BuildExcludeRows();
        UpdateFileStatus();

        _statusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _statusTimer.Tick += (_, _) => UpdateFileStatus();
        _statusTimer.Start();
    }

    /// <summary>Parses the whitelist editor: one extension per line, # comments, dot-prefixed.</summary>
    private void CommitFileExtensions()
    {
        var extensions = FileExtBox.Text.Split('\n', '\r')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith("#"))
            .Select(line => line.StartsWith(".") ? line : "." + line)
            .Select(line => line.ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var normalized = string.Join("\n", extensions);
        FileExtBox.Text = normalized;
        FileExtBox.CaretIndex = FileExtBox.Text.Length;

        var current = App.Current.Settings.FileIndexExtensions;
        if (current.Count == extensions.Count && !current.Except(extensions).Any()) return;
        App.Current.SetFileIndexExtensions(extensions);
        UpdateFileStatus();
    }

    private void OnResetFileExtensions(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings().FileIndexExtensions;
        FileExtBox.Text = string.Join(Environment.NewLine, defaults);
        App.Current.SetFileIndexExtensions(defaults);
        UpdateFileStatus();
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
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var label = new TextBlock
            {
                Text = root,
                Style = (Style)Resources["Hint"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
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
                HorizontalAlignment = HorizontalAlignment.Right,
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

    /// <summary>Blacklist rows: plain paths with a remove link, plus the add button below.</summary>
    private void BuildExcludeRows()
    {
        ExcludeRows.Children.Clear();
        var excludes = App.Current.Settings.FileIndexExcludes;

        if (excludes.Count == 0)
        {
            ExcludeRows.Children.Add(new TextBlock
            {
                Text = "未设置排除目录：索引会遍历上面所有目录的全部子文件夹。",
                Style = (Style)Resources["Hint"],
                Margin = new Thickness(2, 3, 0, 3),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var exclude in excludes)
        {
            var row = new Grid { Margin = new Thickness(2, 2, 0, 2), Tag = exclude };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });

            var label = new TextBlock
            {
                Text = exclude,
                Style = (Style)Resources["Hint"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                ToolTip = Environment.ExpandEnvironmentVariables(exclude)
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            var remove = new Button
            {
                Content = "移除",
                Style = (Style)Resources["LinkButton"],
                Padding = new Thickness(0, 2, 0, 2),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right,
                Tag = exclude
            };
            remove.Click += OnRemoveExclude;
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);

            ExcludeRows.Children.Add(row);
        }
    }

    private void OnAddExcludeVariable(object sender, RoutedEventArgs e)
    {
        var dialog = new PathEntryDialog { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) AddExclude(dialog.EnteredPath);
    }

    private void OnAddExcludeFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择要排除的文件夹",
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        if (string.IsNullOrWhiteSpace(dialog.FolderName)) return;

        AddExclude(dialog.FolderName);
    }

    private void AddExclude(string value)
    {
        var excludes = App.Current.Settings.FileIndexExcludes;
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (excludes.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(Environment.ExpandEnvironmentVariables(x), expanded,
                                  StringComparison.OrdinalIgnoreCase))) return;
        App.Current.SetFileIndexExcludes(excludes.Append(value));
        BuildExcludeRows();
        UpdateFileStatus();
    }

    private void OnRemoveExclude(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string exclude }) return;
        App.Current.SetFileIndexExcludes(
            App.Current.Settings.FileIndexExcludes.Where(x => !string.Equals(x, exclude, StringComparison.OrdinalIgnoreCase)));
        BuildExcludeRows();
        UpdateFileStatus();
    }

    private void OnResetExcludes(object sender, RoutedEventArgs e)
    {
        var defaults = new AppSettings().FileIndexExcludes;
        App.Current.SetFileIndexExcludes(defaults);
        BuildExcludeRows();
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

    public void ShowModule(string module)
    {
        DebounceCard.Visibility = module == "search" ? Visibility.Visible : Visibility.Collapsed;
        FileIndexCard.Visibility = module == "files" ? Visibility.Visible : Visibility.Collapsed;
        FileExtCard.Visibility = module == "extensions" ? Visibility.Visible : Visibility.Collapsed;
        ExcludeCard.Visibility = module == "excludes" ? Visibility.Visible : Visibility.Collapsed;
        EngineCard.Visibility = module == "engines" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildEngineRows()
    {
        EngineRows.Children.Clear();
        var rules = App.Current.Settings.SearchJumpRules ?? new List<SearchJumpRule>();
        for (var index = 0; index < rules.Count; index++)
        {
            var rule = rules[index];
            var row = new Grid { Margin = new Thickness(0), MinHeight = 64 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var details = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 7, 12, 7) };
            details.Children.Add(new TextBlock
            {
                Text = rule.Name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("InkBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            details.Children.Add(new TextBlock
            {
                Text = rule.UrlTemplate,
                FontSize = 10.5,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = (Brush)FindResource("InkFaintBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = rule.UrlTemplate
            });
            row.Children.Add(details);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 5, 0)
            };
            var toggle = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                VerticalAlignment = VerticalAlignment.Center,
                IsChecked = rule.IsEnabled,
                Tag = rule.Id
            };
            toggle.Click += OnEngineToggled;
            actions.Children.Add(toggle);

            var moveUp = MakeOrderButton("↑", "上移，提升搜索显示优先级", rule.Id);
            moveUp.IsEnabled = index > 0;
            moveUp.Click += OnMoveSearchJumpUp;
            actions.Children.Add(moveUp);

            var moveDown = MakeOrderButton("↓", "下移，降低搜索显示优先级", rule.Id);
            moveDown.IsEnabled = index < rules.Count - 1;
            moveDown.Click += OnMoveSearchJumpDown;
            actions.Children.Add(moveDown);

            var edit = new Button
            {
                Content = "编辑",
                Style = (Style)Resources["LinkButton"],
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(7, 0, 0, 0),
                FontSize = 11,
                Tag = rule.Id
            };
            edit.Click += OnEditSearchJump;
            actions.Children.Add(edit);

            var remove = new Button
            {
                Content = "删除",
                Style = (Style)Resources["LinkButton"],
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 11,
                Margin = new Thickness(2, 0, 0, 0),
                Tag = rule.Id
            };
            remove.Click += OnRemoveSearchJump;
            actions.Children.Add(remove);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            EngineRows.Children.Add(row);
            if (index < rules.Count - 1)
                EngineRows.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(8, 0, 8, 0),
                    Background = (Brush)FindResource("CardBorderBrush")
                });
        }
        if (rules.Count == 0)
        {
            EngineRows.Children.Add(new TextBlock
            {
                Text = "还没有搜索跳转，点击下方按钮添加。",
                Style = (Style)Resources["Hint"],
                Margin = new Thickness(8, 12, 8, 12)
            });
        }
    }

    private Button MakeOrderButton(string glyph, string tooltip, string id) => new()
    {
        Content = glyph,
        Style = (Style)Resources["LinkButton"],
        Padding = new Thickness(5, 4, 5, 4),
        Margin = new Thickness(3, 0, 0, 0),
        FontSize = 13,
        ToolTip = tooltip,
        Tag = id
    };

    private void OnEngineToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } clicked) return;
        var rules = App.Current.Settings.SearchJumpRules;
        var rule = rules?.FirstOrDefault(item => item.Id == id);
        if (rule is null) return;
        rule.IsEnabled = clicked.IsChecked == true;
        App.Current.SetSearchJumpRules(rules!);
    }

    private void OnAddSearchJump(object sender, RoutedEventArgs e)
    {
        EditSearchJump(null);
    }

    private void OnEditSearchJump(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
            EditSearchJump(App.Current.Settings.SearchJumpRules?.FirstOrDefault(rule => rule.Id == id));
    }

    private void EditSearchJump(SearchJumpRule? rule)
    {
        var dialog = new SearchJumpEditorDialog(rule) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.ResultRule is not { } result) return;

        var rules = App.Current.Settings.SearchJumpRules?.ToList() ?? new List<SearchJumpRule>();
        var index = rules.FindIndex(item => item.Id == result.Id);
        if (index < 0) rules.Add(result);
        else rules[index] = result;
        App.Current.SetSearchJumpRules(rules);
        BuildEngineRows();
    }

    private void OnRemoveSearchJump(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;
        var rules = App.Current.Settings.SearchJumpRules;
        if (rules is null) return;
        App.Current.SetSearchJumpRules(rules.Where(rule => rule.Id != id));
        BuildEngineRows();
    }

    private void OnMoveSearchJumpUp(object sender, RoutedEventArgs e) => MoveSearchJump(sender, -1);

    private void OnMoveSearchJumpDown(object sender, RoutedEventArgs e) => MoveSearchJump(sender, 1);

    private void MoveSearchJump(object sender, int offset)
    {
        if (sender is not Button { Tag: string id }) return;
        var rules = App.Current.Settings.SearchJumpRules?.ToList();
        if (rules is null) return;
        var index = rules.FindIndex(rule => rule.Id == id);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= rules.Count) return;
        (rules[index], rules[target]) = (rules[target], rules[index]);
        App.Current.SetSearchJumpRules(rules);
        BuildEngineRows();
    }

}
