using System.Windows;
using System.Windows.Controls;
using DesktopJumpList.Models;
using Forms = System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopJumpList;

public partial class AddShortcutWindow : Window
{
    private readonly ShortcutItem? _existing;
    public ShortcutItem? Shortcut { get; private set; }

    public AddShortcutWindow(ShortcutItem? existing = null)
    {
        _existing = existing;
        InitializeComponent();
        if (existing is null) return;

        Title = "编辑快捷项";
        NameBox.Text = existing.Name;
        TargetBox.Text = existing.Target;
        GroupBox.Text = existing.Group;
        TypeBox.SelectedIndex = (int)existing.Type;
    }

    private ShortcutType SelectedType => Enum.Parse<ShortcutType>(((ComboBoxItem)TypeBox.SelectedItem).Tag.ToString()!);

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BrowseButton is null) return;
        BrowseButton.IsEnabled = SelectedType != ShortcutType.Url;
        if (SelectedType == ShortcutType.Url) BrowseButton.ToolTip = "网页链接不需要浏览";
        else BrowseButton.ToolTip = null;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        switch (SelectedType)
        {
            case ShortcutType.Folder:
                using (var dialog = new Forms.FolderBrowserDialog { Description = "选择文件夹" })
                {
                    if (dialog.ShowDialog() == Forms.DialogResult.OK) TargetBox.Text = dialog.SelectedPath;
                }
                break;
            case ShortcutType.File:
            case ShortcutType.Application:
                var picker = new OpenFileDialog { Title = SelectedType == ShortcutType.Application ? "选择应用程序" : "选择文件" };
                if (picker.ShowDialog() == true) TargetBox.Text = picker.FileName;
                break;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var target = TargetBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target))
        {
            MessageBox.Show("请填写名称和目标位置。", "Desktop Jump List", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (SelectedType == ShortcutType.Url && !Uri.TryCreate(target, UriKind.Absolute, out _))
        {
            MessageBox.Show("请输入有效的网址，例如 https://example.com。", "Desktop Jump List", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Shortcut = new ShortcutItem
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Name = name,
            Target = target,
            Type = SelectedType,
            Group = string.IsNullOrWhiteSpace(GroupBox.Text) ? "常用" : GroupBox.Text.Trim(),
            SortOrder = _existing?.SortOrder ?? 0
        };
        DialogResult = true;
    }
}
