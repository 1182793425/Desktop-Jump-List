using System.Windows;
using DesktopJumpList.Models;

namespace DesktopJumpList.Views;

public partial class SearchJumpEditorDialog : Window
{
    private readonly SearchJumpRule? _original;

    public SearchJumpRule? ResultRule { get; private set; }

    public SearchJumpEditorDialog(SearchJumpRule? rule = null)
    {
        InitializeComponent();
        _original = rule?.Copy();
        if (_original is null) return;

        DialogTitle.Text = "编辑搜索跳转";
        NameBox.Text = _original.Name;
        UrlBox.Text = _original.UrlTemplate;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var template = UrlBox.Text.Trim();
        if (name.Length == 0)
        {
            ErrorText.Text = "请填写显示名称。";
            NameBox.Focus();
            return;
        }

        if (!template.Contains("{input}", StringComparison.Ordinal)
            || !Uri.TryCreate(template.Replace("{input}", "query", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorText.Text = "请输入有效的 http/https URL，并包含 {input} 占位符。";
            UrlBox.Focus();
            return;
        }

        ResultRule = _original ?? new SearchJumpRule();
        ResultRule.Name = name;
        ResultRule.UrlTemplate = template;
        if (string.IsNullOrEmpty(ResultRule.Monogram)) ResultRule.Monogram = name[..1];
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
