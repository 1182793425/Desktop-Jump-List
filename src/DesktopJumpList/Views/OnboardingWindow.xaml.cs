using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DesktopJumpList.Services;

namespace DesktopJumpList.Views;

/// <summary>
/// First-run onboarding wizard: welcome → pick drives to index → hotkey → plain-search switch →
/// three screenshots plus the summary. Driven by !AppSettings.FirstRunDone; the flag is committed
/// when the window closes, whatever the exit path. Every page is one short line plus the control
/// it is about — the wizard teaches by showing, not by explaining.
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
        "普通搜索要不要带文件",
        "怎么用",
    ];

    private static readonly string[] StepSubs =
    [
        "碰一下滑出，点一下打开",
        "勾选后点下一步，索引在后台建立",
        "任何窗口下都能呼出面板",
        "开启后，普通搜索也会找本地文件",
        "左右翻看三张图",
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
        HeaderSub.Text = StepSubs[_step];

        for (var i = 0; i < StepCount; i++)
        {
            var dot = (Ellipse)FindName($"Dot{i}")!;
            dot.Fill = i <= _step
                ? (Brush)FindResource("AccentBrush")
                : (Brush)FindResource("InkFaintBrush");
        }

        BackButton.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        // No gate and no skip: 下一步 always works, and building the index stays a plain
        // optional action on its own page instead of a lock on the wizard.
        NextButton.Content = _step == 4 ? "完成 ✓" : "下一步 →";
        if (_step == 4) ShowSlide(_slide, 0);
        PageScroller.ScrollToTop();
    }

    /// <summary>Preview/self-test hook: would a press at this screen point drag the window?</summary>
    internal bool WouldDragAt(Point screenPoint) => NativeSurfaceDrag.WouldDrag(this, HitsInteractive, screenPoint);

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    /// <summary>Preview hook (--onboarding-preview): jump straight to a step and fill its dynamic content.</summary>
    public void ShowPreviewStep(int step)
    {
        ShowStep(step);
        if (step == 4) ShowSlide(0, 0);
    }

    /// <summary>Preview hook: which screenshot the carousel shows.</summary>
    public void ShowPreviewSlide(int index) => ShowSlide(index, 0);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step == 4)
        {
            Finish();
            return;
        }
        if (_step == 1) StartIndexInBackground();
        ShowStep(_step + 1);
    }

    /// <summary>
    /// Leaving the drive page is the "go" signal: the wizard has no build button, so the chosen
    /// roots are handed to the index here and the walk runs on its own thread while the user
    /// reads the next pages.
    /// </summary>
    private void StartIndexInBackground()
    {
        var roots = _drives.Where(d => d.Box.IsChecked == true).Select(d => d.Root).ToList();
        if (roots.Count == 0) return;

        App.Current.SetFileSearchRoots(roots);
        if (!App.Current.Settings.FileSearchEnabled) App.Current.SetFileSearchEnabled(true);
        App.Current.StartFileIndex();
    }

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

    // ───────────────────────── Step 2: hotkey ─────────────────────────

    private void RefreshHotkeyUi()
    {
        var s = App.Current.Settings;
        // Stored as key-enum names ("Ctrl+Oem3"); people type and read keys, not enums.
        HotkeyText.Text = _capturingHotkey ? "按下组合键…" : HotkeyService.FormatForDisplay(s.Hotkey);
        HotkeySwitch.IsChecked = s.HotkeyEnabled;
        HotkeyHint.Text = _capturingHotkey
            ? "按下组合键，Esc 取消"
            : s.HotkeyEnabled ? "数字 1 左边那个键 · 点键框可更换" : "打开「启用」后生效";
        RefreshKeycaps();
    }

    /// <summary>
    /// Draws the current combination as keycaps: Ctrl + `, one cap per part. Punctuation keys get
    /// the shifted symbol above the base one, like the legend on a real keyboard, because a bare
    /// backtick is a single faint mark that reads as an empty key.
    /// </summary>
    private void RefreshKeycaps()
    {
        if (KeycapRow is null) return;
        KeycapRow.Children.Clear();

        var parts = HotkeyService.ParseParts(App.Current.Settings.Hotkey);
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) KeycapRow.Children.Add(new TextBlock { Style = (Style)FindResource("KeyCapPlus") });

            var label = HotkeyService.PrettyKey(parts[i]);
            var cap = new Border { Style = (Style)FindResource("KeyCap") };
            if (label.Length == 1 && ShiftedLegend.TryGetValue(label, out var shifted))
            {
                var legend = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                legend.Children.Add(new TextBlock
                {
                    Text = shifted,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("InkFaintBrush"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                legend.Children.Add(new TextBlock
                {
                    Text = label,
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 22,
                    LineHeight = 18,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom
                });
                cap.Child = legend;
            }
            else
            {
                cap.Child = new TextBlock { Style = (Style)FindResource("KeyCapText"), Text = label };
            }

            KeycapRow.Children.Add(cap);
        }

        KeycapRow.Opacity = App.Current.Settings.HotkeyEnabled ? 1 : 0.4;
    }

    /// <summary>Legend printed above the base character on a key cap.</summary>
    private static readonly Dictionary<string, string> ShiftedLegend = new()
    {
        ["`"] = "~",
        ["-"] = "_",
        ["="] = "+",
        ["["] = "{",
        ["]"] = "}",
        ["\\"] = "|",
        [";"] = ":",
        ["'"] = "\"",
        [","] = "<",
        ["."] = ">",
        ["/"] = "?",
    };

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
        if (!_capturingHotkey)
        {
            // Arrow keys flip the carousel on the last page — the same gesture as the buttons.
            if (_step == 4 && e.Key is Key.Left or Key.Right)
            {
                ShowSlide(_slide + (e.Key == Key.Right ? 1 : -1), e.Key == Key.Right ? 1 : -1);
                e.Handled = true;
            }
            return;
        }
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
        if (mods == ModifierKeys.None && !HotkeyService.AllowsStandalone(vk))
        {
            RefreshHotkeyUi();
            HotkeyHint.Text = "这个键要加修饰键（如 Ctrl）";
            return;
        }

        var text = FormatHotkey(mods, key);
        if (App.Current.TrySetHotkey(text, out var error))
            HotkeyHint.Text = "已设置";
        else
            HotkeyHint.Text = error ?? "无法注册，换一个";
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
        if (!ok) HotkeyHint.Text = error ?? "无法启用";
    }

    // ───────────────────────── Step 3: plain search ─────────────────────────

    private void OnPlainSearchChanged(object sender, RoutedEventArgs e) =>
        App.Current.SetFileSearchOnPlainQuery(PlainSearchSwitch.IsChecked == true);

    // ───────────────────────── Step 4: screenshot carousel ─────────────────────────

    private static readonly (string Source, string Caption)[] Slides =
    [
        ("/Assets/onboarding-panel.png", "面板：常用的都在这，点一下就开"),
        ("/Assets/onboarding-search.png", "搜索：打字即搜，vscode 也能命中"),
        ("/Assets/onboarding-files.png", "/s 关键词：全盘找文件"),
    ];

    private readonly BitmapImage?[] _slideImages = new BitmapImage?[Slides.Length];
    private int _slide;

    private void OnPrevSlide(object sender, RoutedEventArgs e) => ShowSlide(_slide - 1, -1);
    private void OnNextSlide(object sender, RoutedEventArgs e) => ShowSlide(_slide + 1, 1);

    /// <summary>
    /// One screenshot at a time. Direction drives the little slide-in (the incoming image enters
    /// from the side the user is heading to), so paging reads as a carousel instead of a swap.
    /// </summary>
    private void ShowSlide(int index, int direction)
    {
        if (Slides.Length == 0) return;
        index = Math.Clamp(index, 0, Slides.Length - 1);
        _slide = index;

        SlideImage.Source = _slideImages[index] ??= LoadAsset(Slides[index].Source);
        SlideCaption.Text = Slides[index].Caption;

        PrevSlideButton.IsEnabled = index > 0;
        NextSlideButton.IsEnabled = index < Slides.Length - 1;
        RefreshDots();

        if (direction == 0) return;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SlideImage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(170)));
        SlideShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(direction * 26, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
    }

    private static BitmapImage? LoadAsset(string packPath)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri($"pack://application:,,,{packPath}", UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    private void RefreshDots()
    {
        SlideDots.Children.Clear();
        for (var i = 0; i < Slides.Length; i++)
        {
            SlideDots.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(4, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource(i == _slide ? "AccentBrush" : "InkFaintBrush"),
            });
        }
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
