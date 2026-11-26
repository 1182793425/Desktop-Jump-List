using System.Windows;
using System.Windows.Input;

namespace DesktopJumpList.Views;

public partial class PathEntryDialog : Window
{
    public string EnteredPath => PathBox.Text.Trim();

    public PathEntryDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => PathBox.Focus();
    }

    private void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnAdd(sender, e);
        e.Handled = true;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(EnteredPath))
        {
            ErrorText.Text = "请输入要排除的环境变量路径或文件夹路径。";
            PathBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
