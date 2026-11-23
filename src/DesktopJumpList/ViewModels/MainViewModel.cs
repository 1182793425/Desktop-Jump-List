using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopJumpList.Helpers;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private static readonly CultureInfo Zh = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly SolidColorBrush FolderColor = Freeze("#4F7DFF");
    private static readonly SolidColorBrush FileColor = Freeze("#9A7BFF");
    private static readonly SolidColorBrush AppColor = Freeze("#17B8A6");
    private static readonly SolidColorBrush UrlColor = Freeze("#FF8A66");

    private readonly ShortcutStore _store;
    private readonly UsageStore _usageStore;
    private readonly SettingsStore _settingsStore;
    private readonly StartupService _startup;
    private readonly AppSettings _settings;
    private readonly UsageData _usage;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly DispatcherTimer _toastTimer;

    private GroupFilter? _selectedGroup;
    private string _searchText = string.Empty;
    private int _visibleCount;
    private bool _isInsightsOpen;
    private bool _isSettingsOpen;
    private bool _isEditorOpen;
    private bool _startupEnabled;
    private bool _hotkeyAvailable = true;
    private string _toastMessage = string.Empty;
    private bool _isToastVisible;
    private bool _canUndo;
    private Action? _undoAction;
    private int _todayCount;
    private int _weekCount;
    private int _totalOpens;

    public MainViewModel(
        ShortcutStore store,
        UsageStore usageStore,
        SettingsStore settingsStore,
        AppSettings settings,
        StartupService startup,
        string dataDirectory,
        bool preview)
    {
        _store = store;
        _usageStore = usageStore;
        _settingsStore = settingsStore;
        _settings = settings;
        _startup = startup;
        _usage = usageStore.Load();
        DataDirectory = dataDirectory;
        IsPreview = preview;

        ShortcutsView = CollectionViewSource.GetDefaultView(Shortcuts);
        ShortcutsView.Filter = FilterShortcut;
        ShortcutsView.SortDescriptions.Add(new SortDescription(nameof(ShortcutItem.IsPinned), ListSortDirection.Descending));
        ShortcutsView.SortDescriptions.Add(new SortDescription(nameof(ShortcutItem.SortOrder), ListSortDirection.Ascending));

        Editor = new ShortcutEditorViewModel();
        Editor.Saved += OnEditorSaved;
        Editor.Cancelled += CloseEditor;

        OpenCommand = new RelayCommand(parameter => Open(parameter as ShortcutItem));
        SelectGroupCommand = new RelayCommand(parameter => SelectGroup(parameter as GroupFilter));
        AddCommand = new RelayCommand(OpenEditorForNew);
        EditCommand = new RelayCommand(parameter => OpenEditor(parameter as ShortcutItem));
        DeleteCommand = new RelayCommand(parameter => Delete(parameter as ShortcutItem));
        UndoCommand = new RelayCommand(UndoDelete);
        TogglePinCommand = new RelayCommand(parameter => TogglePin(parameter as ShortcutItem));
        RevealCommand = new RelayCommand(parameter => Reveal(parameter as ShortcutItem));
        ShowInsightsCommand = new RelayCommand(ToggleInsights);
        ShowSettingsCommand = new RelayCommand(ToggleSettings);
        CloseOverlaysCommand = new RelayCommand(CloseOverlays);
        ToggleThemeCommand = new RelayCommand(ToggleTheme);
        ClearStatsCommand = new RelayCommand(ClearStats);
        OpenDataFolderCommand = new RelayCommand(() => ShellLauncher.OpenDataFolder(DataDirectory));
        ExitCommand = new RelayCommand(() => ExitRequested?.Invoke());
        DismissToastCommand = new RelayCommand(HideToast);
        ClearSearchCommand = new RelayCommand(() => SearchText = string.Empty);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3800) };
        _toastTimer.Tick += (_, _) => HideToast();
        ThemeManager.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(IsDark));
            OnPropertyChanged(nameof(ThemeToggleHint));
        };
    }

    public event Action? CloseRequested;
    public event Action? ExitRequested;

    public ObservableCollection<ShortcutItem> Shortcuts { get; } = new();
    public ICollectionView ShortcutsView { get; }
    public ObservableCollection<GroupFilter> Groups { get; } = new();
    public ObservableCollection<DayBar> WeekBars { get; } = new();
    public ObservableCollection<DonutSegment> TypeSegments { get; } = new();
    public ObservableCollection<TopShortcut> TopShortcuts { get; } = new();
    public ShortcutEditorViewModel Editor { get; }

    public ICommand OpenCommand { get; }
    public ICommand SelectGroupCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand UndoCommand { get; }
    public ICommand TogglePinCommand { get; }
    public ICommand RevealCommand { get; }
    public ICommand ShowInsightsCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand CloseOverlaysCommand { get; }
    public ICommand ToggleThemeCommand { get; }
    public ICommand ClearStatsCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand ExitCommand { get; }
    public ICommand DismissToastCommand { get; }
    public ICommand ClearSearchCommand { get; }

    public string DataDirectory { get; }
    public bool IsPreview { get; }
    public string VersionText { get; } = $"Desktop Jump List · 2.0.0";
    public string HotkeyDisplay => string.Join(" + ", _settings.Hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    public string DataFolderDisplay => DataDirectory;

    public bool HotkeyAvailable
    {
        get => _hotkeyAvailable;
        set => SetProperty(ref _hotkeyAvailable, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            OnPropertyChanged(nameof(IsSearching));
            RefreshView();
        }
    }

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);

    public int VisibleCount
    {
        get => _visibleCount;
        private set
        {
            if (!SetProperty(ref _visibleCount, value)) return;
            OnPropertyChanged(nameof(HasVisibleShortcuts));
            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(EmptyTitle));
            OnPropertyChanged(nameof(EmptySubtitle));
            OnPropertyChanged(nameof(ListHeader));
        }
    }

    public bool HasVisibleShortcuts => VisibleCount > 0;
    public bool ShowEmptyState => VisibleCount == 0;
    public bool ShowEmptyAddButton => Shortcuts.Count == 0;

    public string ListHeader => IsSearching
        ? $"搜索 “{SearchText.Trim()}”"
        : _selectedGroup is null or { IsAll: true } ? "全部快捷项" : _selectedGroup.Name;

    public string EmptyTitle => Shortcuts.Count == 0
        ? "从第一个快捷项开始"
        : IsSearching ? "没有找到匹配的快捷项" : "这个分组还是空的";

    public string EmptySubtitle => Shortcuts.Count == 0
        ? "把常用的文件夹、应用或网页收进来，两次点击内抵达。"
        : IsSearching ? "换个关键词试试，或清空搜索查看全部内容。" : "切换分组，或添加一个到这个分组。";

    public bool IsInsightsOpen
    {
        get => _isInsightsOpen;
        private set
        {
            if (!SetProperty(ref _isInsightsOpen, value)) return;
            OnPropertyChanged(nameof(OverviewButtonText));
            OnPropertyChanged(nameof(IsShortcutListVisible));
        }
    }

    public bool IsShortcutListVisible => !IsInsightsOpen;

    public string OverviewButtonText => IsInsightsOpen ? "返回快捷" : "使用概览";

    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }

    public bool IsEditorOpen
    {
        get => _isEditorOpen;
        private set => SetProperty(ref _isEditorOpen, value);
    }

    public int TodayCount
    {
        get => _todayCount;
        private set => SetProperty(ref _todayCount, value);
    }

    public int WeekCount
    {
        get => _weekCount;
        private set => SetProperty(ref _weekCount, value);
    }

    public int TotalOpens
    {
        get => _totalOpens;
        private set => SetProperty(ref _totalOpens, value);
    }

    public int TotalShortcuts => Shortcuts.Count;

    public string TodayLabel => DateTime.Today.ToString("M月d日", Zh);
    public string WeekLabel => $"{DateTime.Today.AddDays(-6):M.d} — {DateTime.Today:M.d}";

    public bool StartupEnabled
    {
        get => _startupEnabled;
        set
        {
            if (_startupEnabled == value) return;
            if (IsPreview)
            {
                _startupEnabled = value;
                OnPropertyChanged();
                return;
            }

            try
            {
                _startup.SetEnabled(value);
                _startupEnabled = value;
                OnPropertyChanged();
                ShowToast(value ? "已开启开机启动" : "已关闭开机启动");
            }
            catch (Exception ex)
            {
                OnPropertyChanged();
                ShowToast($"设置失败：{ex.Message}");
            }
        }
    }

    public AppTheme ThemeMode
    {
        get => _settings.Theme;
        set
        {
            if (_settings.Theme == value) return;
            _settings.Theme = value;
            ThemeManager.Apply(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDark));
            _ = SaveSettingsAsync();
        }
    }

    public bool IsDark => ThemeManager.IsDark;
    public string ThemeToggleHint => IsDark ? "切换到浅色" : "切换到深色";

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool IsToastVisible
    {
        get => _isToastVisible;
        private set => SetProperty(ref _isToastVisible, value);
    }

    public bool CanUndo
    {
        get => _canUndo;
        private set => SetProperty(ref _canUndo, value);
    }

    public async Task InitializeAsync()
    {
        var items = await _store.LoadAsync();
        Shortcuts.Clear();
        foreach (var item in items) Shortcuts.Add(item);
        _startupEnabled = _startup.IsEnabled();
        OnPropertyChanged(nameof(StartupEnabled));
        OnPropertyChanged(nameof(TotalShortcuts));

        RebuildGroups();
        RefreshView();
        RefreshStats();
    }

    public void OpenInsights()
    {
        IsSettingsOpen = false;
        IsEditorOpen = false;
        IsInsightsOpen = true;
        RefreshStats();
    }

    public void OpenSettings()
    {
        HideToast();
        IsInsightsOpen = false;
        IsEditorOpen = false;
        IsSettingsOpen = true;
    }

    public void OpenEditorForNew()
    {
        HideToast();
        IsInsightsOpen = false;
        IsSettingsOpen = false;
        Editor.Load(null, Groups.Where(group => !group.IsAll).Select(group => group.Name));
        IsEditorOpen = true;
    }

    public void OpenEditor(ShortcutItem? item)
    {
        if (item is null) return;
        HideToast();
        IsInsightsOpen = false;
        IsSettingsOpen = false;
        Editor.Load(item, Groups.Where(group => !group.IsAll).Select(group => group.Name));
        IsEditorOpen = true;
    }

    public void CloseEditor()
    {
        IsEditorOpen = false;
    }

    public void AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        var skipped = 0;
        var group = _selectedGroup is { IsAll: false } selected ? selected.Name : "常用";
        var existingTargets = new HashSet<string>(Shortcuts.Select(item => item.Target), StringComparer.OrdinalIgnoreCase);

        foreach (var raw in paths)
        {
            var path = raw.Trim();
            ShortcutType type;
            if (Directory.Exists(path)) type = ShortcutType.Folder;
            else if (File.Exists(path)) type = Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                                            Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase)
                ? ShortcutType.Application
                : ShortcutType.File;
            else continue;

            if (!existingTargets.Add(path))
            {
                skipped++;
                continue;
            }

            var name = type == ShortcutType.Folder
                ? new DirectoryInfo(path).Name
                : Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileName(path);

            Shortcuts.Add(new ShortcutItem
            {
                Name = name,
                Target = path,
                Type = type,
                Group = group,
                SortOrder = NextSortOrder(),
                CreatedAt = DateTimeOffset.Now
            });
            added++;
        }

        if (added == 0)
        {
            ShowToast(skipped > 0 ? "这些目标已经在列表里了" : "没有识别到可添加的文件或文件夹");
            return;
        }

        SelectGroup(Groups.FirstOrDefault());
        SearchText = string.Empty;
        RebuildGroups();
        RefreshView();
        OnPropertyChanged(nameof(TotalShortcuts));
        _ = PersistAsync();
        ShowToast(skipped > 0 ? $"已添加 {added} 个快捷项，忽略 {skipped} 个重复项" : $"已添加 {added} 个快捷项");
    }

    public void SavePlacement(string edge, double topRatio)
    {
        _settings.Edge = edge;
        _settings.TopRatio = Math.Clamp(topRatio, 0, 1);
        _settings.HasPlacedWindow = true;
        _ = SaveSettingsAsync();
    }

    public async Task SaveSettingsAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex)
        {
            ShowToast($"设置保存失败：{ex.Message}");
        }
    }

    public void SavePlacementNow(string edge, double topRatio)
    {
        _settings.Edge = edge;
        _settings.TopRatio = Math.Clamp(topRatio, 0, 1);
        _settings.HasPlacedWindow = true;
        try
        {
            _settingsStore.Save(_settings);
        }
        catch
        {
            // Shutdown must never be blocked by a settings write.
        }
    }

    private void Open(ShortcutItem? item)
    {
        if (item is null) return;
        try
        {
            ShellLauncher.Open(item);
            item.OpenCount++;
            item.LastOpenedAt = DateTimeOffset.Now;
            _usage.Record(DateTime.Today);
            RefreshStats();
            _ = PersistAsync();
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            ShowToast(ShellLauncher.FriendlyError(ex));
        }
    }

    private void Reveal(ShortcutItem? item)
    {
        if (item is null) return;
        try
        {
            ShellLauncher.RevealInExplorer(item);
            CloseRequested?.Invoke();
        }
        catch (Exception ex)
        {
            ShowToast(ShellLauncher.FriendlyError(ex));
        }
    }

    private void Delete(ShortcutItem? item)
    {
        if (item is null) return;
        var index = Shortcuts.IndexOf(item);
        if (index < 0) return;
        Shortcuts.RemoveAt(index);
        RebuildGroups();
        RefreshView();
        RefreshStats();
        OnPropertyChanged(nameof(TotalShortcuts));
        _ = PersistAsync();
        ShowToast($"已删除「{item.Name}」", () =>
        {
            Shortcuts.Insert(Math.Min(index, Shortcuts.Count), item);
            RebuildGroups();
            RefreshView();
            RefreshStats();
            OnPropertyChanged(nameof(TotalShortcuts));
            _ = PersistAsync();
        });
    }

    private void UndoDelete()
    {
        var action = _undoAction;
        HideToast();
        action?.Invoke();
    }

    private void TogglePin(ShortcutItem? item)
    {
        if (item is null) return;
        item.IsPinned = !item.IsPinned;
        ShortcutsView.Refresh();
        _ = PersistAsync();
        ShowToast(item.IsPinned ? $"已将「{item.Name}」置顶" : $"已取消置顶「{item.Name}」");
    }

    private void OnEditorSaved(ShortcutItem item, bool isNew)
    {
        var oldTarget = isNew ? null : Shortcuts.FirstOrDefault(existing => existing.Id == item.Id)?.Target;
        if (isNew)
        {
            item.SortOrder = NextSortOrder();
            item.CreatedAt = DateTimeOffset.Now;
            Shortcuts.Add(item);
        }
        else
        {
            var index = Shortcuts.ToList().FindIndex(existing => existing.Id == item.Id);
            if (index >= 0) Shortcuts[index] = item;
            else Shortcuts.Add(item);
        }

        IconService.Invalidate(oldTarget);
        IconService.Invalidate(item.Target);
        CloseEditor();
        if (isNew || _selectedGroup is { IsAll: false } group && !string.Equals(group.Name, item.Group, StringComparison.CurrentCultureIgnoreCase))
        {
            SelectGroup(Groups.FirstOrDefault());
            SearchText = string.Empty;
        }

        RebuildGroups();
        RefreshView();
        RefreshStats();
        OnPropertyChanged(nameof(TotalShortcuts));
        _ = PersistAsync();
        ShowToast(isNew ? $"已添加「{item.Name}」" : $"已保存「{item.Name}」");
    }

    private int NextSortOrder() => Shortcuts.Count == 0 ? 0 : Shortcuts.Max(item => item.SortOrder) + 1;

    private void ToggleInsights()
    {
        IsSettingsOpen = false;
        IsEditorOpen = false;
        IsInsightsOpen = !IsInsightsOpen;
        if (IsInsightsOpen) RefreshStats();
    }

    private void ToggleSettings()
    {
        IsInsightsOpen = false;
        IsEditorOpen = false;
        IsSettingsOpen = !IsSettingsOpen;
    }

    private void CloseOverlays()
    {
        IsSettingsOpen = false;
        IsEditorOpen = false;
        IsInsightsOpen = false;
    }

    private void ToggleTheme()
    {
        ThemeMode = ThemeManager.IsDark ? AppTheme.Light : AppTheme.Dark;
        ShowToast($"已切换到{(ThemeManager.IsDark ? "深色" : "浅色")}外观");
    }

    private void ClearStats()
    {
        _usage.Clear();
        foreach (var shortcut in Shortcuts)
        {
            shortcut.OpenCount = 0;
            shortcut.LastOpenedAt = null;
        }

        RefreshStats();
        _ = PersistAsync();
        ShowToast("使用统计已清空");
    }

    private void SelectGroup(GroupFilter? group)
    {
        if (group is null) return;
        _selectedGroup = group;
        foreach (var candidate in Groups) candidate.IsSelected = ReferenceEquals(candidate, group);
        RefreshView();
    }

    private void RebuildGroups()
    {
        var selectedName = _selectedGroup is { IsAll: false } ? _selectedGroup.Name : null;
        var all = new GroupFilter(string.Empty, true) { Count = Shortcuts.Count };
        Groups.Clear();
        Groups.Add(all);
        foreach (var group in Shortcuts
                     .GroupBy(item => item.Group)
                     .OrderByDescending(group => group.Count())
                     .ThenBy(group => group.Key, StringComparer.CurrentCulture))
        {
            Groups.Add(new GroupFilter(group.Key, false) { Count = group.Count() });
        }

        _selectedGroup = selectedName is null
            ? all
            : Groups.FirstOrDefault(candidate => !candidate.IsAll && candidate.Name == selectedName) ?? all;
        foreach (var candidate in Groups) candidate.IsSelected = ReferenceEquals(candidate, _selectedGroup);
        OnPropertyChanged(nameof(ListHeader));
    }

    private void RefreshView()
    {
        ShortcutsView.Refresh();
        VisibleCount = ShortcutsView.Cast<object>().Count();
        OnPropertyChanged(nameof(ListHeader));
        OnPropertyChanged(nameof(ShowEmptyAddButton));
    }

    private bool FilterShortcut(object item)
    {
        if (item is not ShortcutItem shortcut) return false;
        if (_selectedGroup is { IsAll: false } group &&
            !string.Equals(shortcut.Group, group.Name, StringComparison.CurrentCultureIgnoreCase))
            return false;

        var term = SearchText.Trim();
        if (term.Length == 0) return true;
        return shortcut.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || shortcut.Group.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || shortcut.Target.Contains(term, StringComparison.CurrentCultureIgnoreCase)
               || shortcut.TypeLabel.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshStats()
    {
        var today = DateTime.Today;
        WeekBars.Clear();
        for (var offset = -6; offset <= 0; offset++)
        {
            var date = today.AddDays(offset);
            var label = date == today ? "今天" : date.ToString("ddd", Zh);
            WeekBars.Add(new DayBar(label, _usage.Count(date), date == today, date));
        }

        TodayCount = _usage.Count(today);
        WeekCount = _usage.CountSince(today.AddDays(-6));
        TotalOpens = Shortcuts.Sum(item => item.OpenCount);

        TypeSegments.Clear();
        var typeGroups = Shortcuts.GroupBy(item => item.Type).OrderByDescending(group => group.Count()).ToList();
        var total = Shortcuts.Count;
        foreach (var group in typeGroups)
        {
            var label = group.Key switch
            {
                ShortcutType.Folder => "文件夹",
                ShortcutType.File => "文件",
                ShortcutType.Application => "应用",
                _ => "网页"
            };
            var color = group.Key switch
            {
                ShortcutType.Folder => FolderColor,
                ShortcutType.File => FileColor,
                ShortcutType.Application => AppColor,
                _ => UrlColor
            };
            TypeSegments.Add(new DonutSegment(label, group.Count(), total == 0 ? 0 : group.Count() * 100.0 / total, color));
        }

        TopShortcuts.Clear();
        var top = Shortcuts.OrderByDescending(item => item.OpenCount)
            .ThenByDescending(item => item.LastOpenedAt)
            .Take(4)
            .ToList();
        var max = top.Count == 0 ? 1 : Math.Max(1, top.Max(item => item.OpenCount));
        for (var i = 0; i < top.Count; i++)
            TopShortcuts.Add(new TopShortcut(i + 1, top[i], top[i].OpenCount * 100.0 / max));

        OnPropertyChanged(nameof(TodayCount));
        OnPropertyChanged(nameof(WeekCount));
        OnPropertyChanged(nameof(TotalOpens));
        OnPropertyChanged(nameof(TotalShortcuts));
        OnPropertyChanged(nameof(TodayLabel));
        OnPropertyChanged(nameof(WeekLabel));
    }

    private void ShowToast(string message, Action? undo = null)
    {
        ToastMessage = message;
        CanUndo = undo is not null;
        _undoAction = undo;
        IsToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        IsToastVisible = false;
        CanUndo = false;
        _undoAction = null;
    }

    private async Task PersistAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            await _store.SaveAsync(Shortcuts);
            await _usageStore.SaveAsync(_usage);
        }
        catch (Exception ex)
        {
            ShowToast($"保存失败：{ex.Message}");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
