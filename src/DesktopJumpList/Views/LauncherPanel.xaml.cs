using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DesktopJumpList.Models;
using DesktopJumpList.Services;
using DesktopJumpList.ViewModels;
using Forms = System.Windows.Forms;

namespace DesktopJumpList.Views;

public partial class LauncherPanel : UserControl
{
    public static readonly DependencyProperty ContentOpacityProperty = DependencyProperty.Register(
        nameof(ContentOpacity), typeof(double), typeof(LauncherPanel),
        new PropertyMetadata(1.0, (d, e) =>
        {
            var panel = (LauncherPanel)d;
            var value = (double)e.NewValue;
            panel.ContentLayer.Opacity = value;
            panel.ContentLayer.IsHitTestVisible = value > 0.55;
        }));

    private MainViewModel? _viewModel;

    public double ContentOpacity
    {
        get => (double)GetValue(ContentOpacityProperty);
        set => SetValue(ContentOpacityProperty, value);
    }

    public LauncherPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel) SyncOverlays(viewModel);
        };
    }

    public void FocusSearch()
    {
        Dispatcher.BeginInvoke(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
            SearchBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public void FocusEditor()
    {
        Dispatcher.BeginInvoke(() =>
        {
            EditorNameBox.Focus();
            Keyboard.Focus(EditorNameBox);
            EditorNameBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    public void FocusSettings()
    {
        Dispatcher.BeginInvoke(() => Keyboard.Focus(SettingsDoneButton), DispatcherPriority.Input);
    }

    public void MoveSelection(int delta)
    {
        if (ShortcutList.Items.Count == 0) return;
        var index = ShortcutList.SelectedIndex;
        index = index < 0
            ? (delta >= 0 ? 0 : ShortcutList.Items.Count - 1)
            : Math.Clamp(index + delta, 0, ShortcutList.Items.Count - 1);
        ShortcutList.SelectedIndex = index;
        ShortcutList.ScrollIntoView(ShortcutList.SelectedItem);
    }

    public void ScrollInsightsTo(double offset)
    {
        InsightsScroll.ScrollToVerticalOffset(offset);
    }

    public void ResetState()
    {
        ShortcutList.SelectedIndex = -1;
        if (ShortcutList.Items.Count > 0) ShortcutList.ScrollIntoView(ShortcutList.Items[0]);
        InsightsScroll.ScrollToTop();
    }

    public void OpenSelected()
    {
        var item = ShortcutList.SelectedItem as ShortcutItem
                   ?? ShortcutList.Items.Cast<object>().OfType<ShortcutItem>().FirstOrDefault();
        if (item is not null && DataContext is MainViewModel viewModel) viewModel.OpenCommand.Execute(item);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            SyncOverlays(_viewModel);
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsEditorOpen):
                AnimateEditor(_viewModel.IsEditorOpen);
                if (_viewModel.IsEditorOpen) FocusEditor();
                else if (!_viewModel.IsInsightsOpen) FocusSearch();
                break;
            case nameof(MainViewModel.IsSettingsOpen):
                AnimateSettings(_viewModel.IsSettingsOpen);
                if (_viewModel.IsSettingsOpen) FocusSettings();
                else if (!_viewModel.IsInsightsOpen) FocusSearch();
                break;
            case nameof(MainViewModel.IsToastVisible):
                AnimateToast(_viewModel.IsToastVisible);
                break;
            case nameof(MainViewModel.IsInsightsOpen):
                if (_viewModel.IsInsightsOpen) InsightsScroll.ScrollToTop();
                break;
        }
    }

    private void SyncOverlays(MainViewModel viewModel)
    {
        if (viewModel.IsEditorOpen) AnimateEditor(true, animate: false);
        if (viewModel.IsSettingsOpen) AnimateSettings(true, animate: false);
        if (viewModel.IsToastVisible) AnimateToast(true, animate: false);
    }

    private void AnimateEditor(bool show, bool animate = true)
    {
        if (show)
        {
            EditorOverlay.Visibility = Visibility.Visible;
            EditorOverlay.BeginAnimation(OpacityProperty, null);
            EditorTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            if (!animate)
            {
                EditorOverlay.Opacity = 1;
                EditorTranslate.Y = 0;
                return;
            }

            EditorOverlay.Opacity = 0;
            EditorTranslate.Y = 24;
            EditorOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            EditorTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            var fade = new DoubleAnimation(EditorOverlay.Opacity, 0, TimeSpan.FromMilliseconds(130))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            fade.Completed += (_, _) =>
            {
                if (_viewModel?.IsEditorOpen == false) EditorOverlay.Visibility = Visibility.Collapsed;
            };
            EditorOverlay.BeginAnimation(OpacityProperty, fade);
            EditorTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(EditorTranslate.Y, 14, TimeSpan.FromMilliseconds(130)));
        }
    }

    private void AnimateSettings(bool show, bool animate = true)
    {
        if (show)
        {
            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsOverlay.BeginAnimation(OpacityProperty, null);
            SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            if (!animate)
            {
                SettingsOverlay.Opacity = 1;
                SettingsScale.ScaleX = SettingsScale.ScaleY = 1;
                return;
            }

            SettingsOverlay.Opacity = 0;
            SettingsScale.ScaleX = SettingsScale.ScaleY = 0.98;
            SettingsOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.98, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }
        else
        {
            var fade = new DoubleAnimation(SettingsOverlay.Opacity, 0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) =>
            {
                if (_viewModel?.IsSettingsOpen == false) SettingsOverlay.Visibility = Visibility.Collapsed;
            };
            SettingsOverlay.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void AnimateToast(bool show, bool animate = true)
    {
        if (show)
        {
            Toast.Visibility = Visibility.Visible;
            Toast.BeginAnimation(OpacityProperty, null);
            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            if (!animate)
            {
                Toast.Opacity = 1;
                ToastTranslate.Y = 0;
                return;
            }

            Toast.Opacity = 0;
            ToastTranslate.Y = 8;
            Toast.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            ToastTranslate.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            var fade = new DoubleAnimation(Toast.Opacity, 0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) =>
            {
                if (_viewModel?.IsToastVisible == false) Toast.Visibility = Visibility.Collapsed;
            };
            Toast.BeginAnimation(OpacityProperty, fade);
            ToastTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, 6, TimeSpan.FromMilliseconds(130)));
        }
    }

    private void ClosePanel_Click(object sender, RoutedEventArgs e)
        => (Window.GetWindow(this) as MainWindow)?.HidePanel();

    private void ShortcutList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) is not null) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not ShortcutItem item) return;
        ShortcutList.SelectedItem = item;
        if (DataContext is MainViewModel viewModel) viewModel.OpenCommand.Execute(item);
        e.Handled = true;
    }

    private void Card_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutItem item } target) return;
        OpenItemMenu(target, item, PlacementMode.MousePoint);
        e.Handled = true;
    }

    private void ItemMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutItem item } target) return;
        OpenItemMenu(target, item, PlacementMode.Bottom);
        e.Handled = true;
    }

    private void OpenItemMenu(FrameworkElement target, ShortcutItem item, PlacementMode placement)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = placement,
            StaysOpen = false
        };

        menu.Items.Add(MenuAction("打开", () => viewModel.OpenCommand.Execute(item)));
        menu.Items.Add(MenuAction("编辑", () => viewModel.EditCommand.Execute(item)));
        menu.Items.Add(MenuAction(item.IsPinned ? "取消置顶" : "置顶", () => viewModel.TogglePinCommand.Execute(item)));
        if (item.Type != ShortcutType.Url)
            menu.Items.Add(MenuAction("在资源管理器中显示", () => viewModel.RevealCommand.Execute(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("删除", () => viewModel.DeleteCommand.Execute(item)));
        menu.IsOpen = true;
    }

    private static MenuItem MenuAction(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var editor = viewModel.Editor;
        var window = Window.GetWindow(this) as MainWindow;
        window?.BeginModal();
        try
        {
            switch (editor.Type)
            {
                case ShortcutType.Folder:
                    using (var dialog = new Forms.FolderBrowserDialog
                           {
                               Description = "选择要加入快捷面板的文件夹",
                               UseDescriptionForTitle = true,
                               ShowNewFolderButton = true
                           })
                    {
                        if (dialog.ShowDialog() == Forms.DialogResult.OK) editor.SetTargetFromPicker(dialog.SelectedPath);
                    }

                    break;
                case ShortcutType.File:
                case ShortcutType.Application:
                    var picker = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = editor.Type == ShortcutType.Application ? "选择应用程序" : "选择文件",
                        Filter = editor.Type == ShortcutType.Application
                            ? "应用程序 (*.exe;*.lnk)|*.exe;*.lnk|所有文件 (*.*)|*.*"
                            : "所有文件 (*.*)|*.*",
                        CheckFileExists = true
                    };
                    if (picker.ShowDialog() == true) editor.SetTargetFromPicker(picker.FileName);
                    break;
            }
        }
        finally
        {
            window?.EndModal();
        }
    }

    private void GroupSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: string group } && DataContext is MainViewModel viewModel)
            viewModel.Editor.Group = group;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(2);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-2);
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
        }
    }

    private void SetDropVisible(bool visible)
        => DropOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private void Root_DragEnter(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        e.Effects = DragDropEffects.Copy;
        SetDropVisible(true);
        e.Handled = true;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        if (!HasFiles(e)) return;
        e.Effects = DragDropEffects.Copy;
        if (DropOverlay.Visibility != Visibility.Visible) SetDropVisible(true);
        e.Handled = true;
    }

    private void Root_DragLeave(object sender, DragEventArgs e)
    {
        SetDropVisible(false);
        e.Handled = true;
    }

    private void Root_Drop(object sender, DragEventArgs e)
    {
        SetDropVisible(false);
        e.Handled = true;
        if (!HasFiles(e) || DataContext is not MainViewModel viewModel) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) viewModel.AddPaths(paths);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
