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
            ErrorText.Text = "请输入要排除的路径、文件名或通配符，例如 %TEMP%\\Logs、thumbs.db、*.bak。";
            PathBox.Focus();
            return;
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
