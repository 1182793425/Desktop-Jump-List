using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DesktopJumpList.Views;

/// <summary>
/// First-run onboarding wizard: welcome → pick drives to index and start the walk → hotkey →
/// plain-search switch → usage crash course → summary. Driven by !AppSettings.FirstRunDone;
/// the flag is committed when the window closes, whatever the exit path.
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int StepCount = 5; // welcome / index / hotkey / plain search / teach+done share dots

    private sealed class DriveChoice(string root, CheckBox box, TextBlock size)
    {
        public string Root = root;
        public CheckBox Box = box;
        public TextBlock Size = size;
    }

    private readonly List<DriveChoice> _drives = [];
    private bool _capturingHotkey;
    private bool _buildingStarted;

    public OnboardingWindow()
    {
        InitializeComponent();

        // Native WM_NCHITTEST drag: non-interactive surface returns HTCAPTION and Windows
        // runs the drag loop; the window stays borderless and shadow-free.
        NativeSurfaceDrag.Enable(this, HitsInteractive);

        FillDriveList();
        RefreshHotkeyUi();
        PlainSearchSwitch.IsChecked = App.Current.Settings.FileSearchOnPlainQuery;
        ShowStep(0);
    }

    private static bool HitsInteractive(DependencyObject? node)
    {
        while (node is not null)
        {
            // Window derives from Control: stopping here keeps plain page chrome (headings,
            // cards, the empty footer) draggable instead of making the whole wizard a button.
            if (node is Window) return false;
            // The step pages scroll inside one ScrollViewer — it is a Control itself but must
            // not swallow the drag; its ScrollBars are Controls and still will.
            if (node is System.Windows.Controls.ScrollViewer)
            { node = System.Windows.Media.VisualTreeHelper.GetParent(node); continue; }
            // The hotkey picker is a Border with its own MouseLeftButtonDown, not a Control —
            // without this rule a press on it would start a window drag instead of a capture.
            if (node is FrameworkElement { Name: nameof(HotkeyBox) }) return true;
            if (node is System.Windows.Controls.Control) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ───────────────────────── Step flow ─────────────────────────

    private static readonly string[] StepTitles =
    [
        "欢迎使用 Jump",
        "选择要索引的磁盘",
        "设置呼出快捷键",
        "搜索方式：要不要带上本地文件？",
        "怎么用：搜索、打开与重建",
        "设置完成！",
    ];

    private int _step;

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, 4);
        PageWelcome.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageIndex.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageHotkey.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        PagePlainSearch.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageTeach.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;

        HeaderTitle.Text = StepTitles[_step];
        HeaderSub.Text = _step switch
        {
            0 => "三分钟完成设置，之后一切只需一键",
            1 => "第 1 步 · 让 /s 和直接搜索能全盘找文件",
            2 => "第 2 步 · 任何窗口下一键呼出",
            3 => "第 3 步 · 决定普通搜索的结果范围",
            4 => "第 4 步 · 花三十秒看完，少走弯路",
            _ => "",
        };

        for (var i = 0; i < StepCount; i++)
        {
            var dot = (Ellipse)FindName($"Dot{i}")!;
            dot.Fill = i <= _step
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("InkFaintBrush");
        }

        BackButton.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        SkipButton.Visibility = _step >= 1 && _step <= 3 ? Visibility.Visible : Visibility.Collapsed;
        // Step 1 gates on actually starting the index build — the disabled label says so
        // instead of leaving a greyed-out "下一步" that reads as a broken button.
        NextButton.IsEnabled = _step != 1 || _buildingStarted;
        NextButton.Content = _step switch
        {
            0 => "开始配置 →",
            1 when !_buildingStarted => "先点「开始建立索引」",
            4 => "完成 ✓",
            _ => "下一步 →",
        };
    }

    /// <summary>Preview/self-test hook: would a press at this screen point drag the window?</summary>
    internal bool WouldDragAt(Point screenPoint) => NativeSurfaceDrag.WouldDrag(this, HitsInteractive, screenPoint);

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    /// <summary>Preview hook (--onboarding-preview): jump straight to a step and fill its dynamic content.</summary>
    public void ShowPreviewStep(int step)
    {
        ShowStep(step);
        if (step == 4) FillSummary();
    }

    /// <summary>Preview hook: scroll the page area to the bottom (summary card).</summary>
    public void ScrollPreviewToEnd() => PageScroller.ScrollToEnd();

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == 4)
        {
            Finish();
            return;
        }
        if (_step == 3)
        {
            // Last step: usage crash course plus the live configuration summary.
            FillSummary();
            ShowStep(4);
            return;
        }
        ShowStep(_step + 1);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e) => Finish();

    private void Finish()
    {
        App.Current.Settings.FirstRunDone = true;
        App.Current.SaveSettings();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Whatever the exit path, the wizard must not re-appear on every launch.
        if (!App.Current.Settings.FirstRunDone)
        {
            App.Current.Settings.FirstRunDone = true;
            App.Current.SaveSettings();
        }
        base.OnClosed(e);
    }

    // ───────────────────────── Step 1: drives ─────────────────────────

    private void FillDriveList()
    {
        DriveList.Children.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not DriveType.Fixed && drive.DriveType is not DriveType.Removable) continue;
            if (!drive.IsReady) continue;

            var box = new CheckBox
            {
                Style = (Style)FindResource("DriveRow"),
                Tag = $"{drive.Name}  ({drive.DriveLabel()})",
                IsChecked = true // all drives preselected; exclusions are handled by the index blacklist
            };
            var size = new TextBlock();
            // Wire the row sub-caption through the template's named slot: find it after load.
            box.Loaded += (_, _) =>
            {
                box.ApplyTemplate(); // ensure the template is instantiated before FindName
                if (box.Template.FindName("SizeText", box) is TextBlock text)
                {
                    try
                    {
                        var free = drive.AvailableFreeSpace / (1024L * 1024 * 1024);
                        var total = drive.TotalSize / (1024L * 1024 * 1024);
                        text.Text = $"剩余 {free} GB / 共 {total} GB";
                    }
                    catch { }
                }
            };
            _drives.Add(new DriveChoice(drive.Name, box, size));
            DriveList.Children.Add(box);
        }
    }

    private void OnBuildIndexClick(object sender, RoutedEventArgs e)
    {
        var roots = _drives.Where(d => d.Box.IsChecked == true).Select(d => d.Root).ToList();
        if (roots.Count == 0)
        {
            IndexProgress.Text = "请先勾选至少一个盘符";
            return;
        }

        App.Current.SetFileSearchRoots(roots);
        if (!App.Current.Settings.FileSearchEnabled) App.Current.SetFileSearchEnabled(true);

        _buildingStarted = true;
        BuildButton.IsEnabled = false;
        App.Current.StartFileIndex();
        IndexProgress.Text = "索引建立中…";
        NextButton.IsEnabled = true;

        // Live progress: the index reports row counts through Changed.
        App.Current.Files.Changed += OnIndexChanged;
        OnIndexChanged();
    }

    private void OnIndexChanged()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var count = App.Current.Files.Count;
                IndexProgress.Text = App.Current.Files.IsBuilding
                    ? $"索引建立中… 已收录 {count} 项"
                    : $"索引完成，共 {count} 项";
                if (!App.Current.Files.IsBuilding && _buildingStarted)
                {
                    BuildButton.Content = "已建立 ✓";
                    App.Current.Files.Changed -= OnIndexChanged;
                }
            }));
        }
        catch { /* window may be gone */ }
    }

    // ───────────────────────── Step 2: hotkey ─────────────────────────

    private void RefreshHotkeyUi()
    {
        var s = App.Current.Settings;
        HotkeyText.Text = _capturingHotkey ? "按下组合键…" : s.Hotkey;
        HotkeySwitch.IsChecked = s.HotkeyEnabled;
        HotkeyHint.Text = s.HotkeyEnabled
            ? $"当前：{s.Hotkey} · 点击组合键框可更换"
            : "已关闭，打开开关后生效";
    }

    private void OnHotkeyBoxClick(object sender, MouseButtonEventArgs e)
    {
        _capturingHotkey = true;
        RefreshHotkeyUi();
        HotkeyBox.BorderBrush = (Brush)FindResource("AccentBrush");
        Keyboard.Focus(HotkeyBox);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (!_capturingHotkey) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin
            or Key.System or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
            return;

        _capturingHotkey = false;
        HotkeyBox.BorderBrush = (Brush)FindResource("CardBorderBrush");

        if (key == Key.Escape)
        {
            RefreshHotkeyUi();
            return;
        }

        var mods = Keyboard.Modifiers;
        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (mods == ModifierKeys.None && !Services.HotkeyService.AllowsStandalone(vk))
        {
            RefreshHotkeyUi();
            HotkeyHint.Text = "该键已被系统占用，请加一个修饰键（如 Ctrl）";
            return;
        }

        var text = FormatHotkey(mods, key);
        if (App.Current.TrySetHotkey(text, out var error))
            HotkeyHint.Text = $"已设置为 {text}";
        else
            HotkeyHint.Text = error ?? "无法注册该快捷键，换一个试试";
        RefreshHotkeyUi();
    }

    private static string FormatHotkey(ModifierKeys mods, Key key)
    {
        var parts = new List<string>(5);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key)
    {
        var text = new KeyConverter().ConvertToInvariantString(key);
        return string.IsNullOrWhiteSpace(text) ? key.ToString() : text;
    }

    private void OnHotkeySwitchChanged(object sender, RoutedEventArgs e)
    {
        var want = HotkeySwitch.IsChecked == true;
        var ok = App.Current.SetHotkeyEnabled(want, out var error);
        RefreshHotkeyUi();
        if (!ok) HotkeyHint.Text = error ?? "无法启用该快捷键";
    }

    // ───────────────────────── Step 3: plain search ─────────────────────────

    private void OnPlainSearchChanged(object sender, RoutedEventArgs e) =>
        App.Current.SetFileSearchOnPlainQuery(PlainSearchSwitch.IsChecked == true);

    // ───────────────────────── Step 5: summary ─────────────────────────

    private void FillSummary()
    {
        var s = App.Current.Settings;
        SummaryList.Children.Clear();
        AddSummary("索引盘符", s.FileSearchRoots.Count > 0 ? string.Join("  ", s.FileSearchRoots) : "未设置（/s 不可用）");
        AddSummary("索引状态", App.Current.Files.IsBuilding ? "建立中…" : $"已收录 {App.Current.Files.Count} 项");
        AddSummary("全局快捷键", (s.HotkeyEnabled ? "已启用 " : "未启用 ") + s.Hotkey);
        AddSummary("普通搜索带文件", s.FileSearchOnPlainQuery ? "开启" : "关闭（/s 时才检索文件）");
    }

    private void AddSummary(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.Children.Add(new TextBlock { Text = label, FontSize = 11.5, Foreground = (Brush)FindResource("InkSubBrush") });
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)FindResource("InkBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 360,
        };
        grid.Children.Add(valueText);
        SummaryList.Children.Add(grid);
    }
}

/// <summary>Drive label helper: "System" for C:, "新加卷" etc.; falls back to the type name.</summary>
internal static class DriveInfoExtensions
{
    public static string DriveLabel(this DriveInfo drive)
    {
        try
        {
            var label = drive.VolumeLabel;
            if (!string.IsNullOrWhiteSpace(label)) return label;
        }
        catch { }
        return drive.DriveType switch
        {
            DriveType.Fixed => "本地磁盘",
            DriveType.Removable => "可移动磁盘",
            _ => drive.DriveType.ToString(),
        };
    }
}
