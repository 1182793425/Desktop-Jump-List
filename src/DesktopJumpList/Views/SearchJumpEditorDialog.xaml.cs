using System.Windows;
using System.Windows.Media;
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
        ColorBox.Text = _original.ColorHex;
        GlyphBox.Text = _original.Monogram;
        RefreshIconPreview();
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

        var colorText = ColorBox.Text.Trim();
        Color iconColor;
        try
        {
            if (colorText.Length is not (7 or 9) || !colorText.StartsWith('#'))
                throw new FormatException();
            iconColor = (Color)ColorConverter.ConvertFromString(colorText);
        }
        catch
        {
            ErrorText.Text = "图标底色请输入 #RRGGBB 格式，例如 #2B34E8。";
            ColorBox.Focus();
            return;
        }

        ResultRule = _original ?? new SearchJumpRule();
        ResultRule.Name = name;
        ResultRule.UrlTemplate = template;
        ResultRule.ColorHex = iconColor.ToString();
        ResultRule.Monogram = string.IsNullOrWhiteSpace(GlyphBox.Text) ? name[..1] : GlyphBox.Text.Trim();
        DialogResult = true;
    }

    private void OnIconAppearanceChanged(object sender, RoutedEventArgs e) => RefreshIconPreview();

    private void RefreshIconPreview()
    {
        GlyphPreview.Text = string.IsNullOrWhiteSpace(GlyphBox.Text) ? "搜" : GlyphBox.Text.Trim();
        try
        {
            if (ColorConverter.ConvertFromString(ColorBox.Text.Trim()) is Color color)
                IconPreview.Background = new SolidColorBrush(color);
        }
        catch { }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
