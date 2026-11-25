using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;

namespace DesktopJumpList.Views;

/// <summary>
/// The second page of settings: currently the web-search engines that are offered while typing.
/// Opened from the settings window so the main page stays short.
/// </summary>
public partial class AdvancedSettingsWindow : Window
{
    private readonly List<(string Id, CheckBox Toggle)> _toggles = new();

    public AdvancedSettingsWindow()
    {
        InitializeComponent();
        BuildEngineRows();
        WireDebounce();

        SourceInitialized += (_, _) => CenterOnScreen();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
        Loaded += (_, _) => Refresh();
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

    private void CenterOnScreen()
    {
        var area = ScreenGeometry.WorkAreaAtCursor(this);
        var max = Math.Max(360, area.Height - 24);
        if (Height > max) Height = max;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void BuildEngineRows()
    {
        foreach (var engine in SearchEngineCatalog.All)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3), MinHeight = 34 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var badge = new Border
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(7),
                Background = engine.BrandBrush,
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.Child = new TextBlock
            {
                Text = engine.Monogram,
                Foreground = Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(badge);

            var name = new TextBlock
            {
                Text = engine.Name,
                Style = (Style)Resources["RowLabel"],
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);

            var toggle = new CheckBox
            {
                Style = (Style)FindResource("Switch"),
                VerticalAlignment = VerticalAlignment.Center,
                Tag = engine.Id
            };
            toggle.Click += OnEngineToggled;
            Grid.SetColumn(toggle, 2);
            row.Children.Add(toggle);

            _toggles.Add((engine.Id, toggle));
            EngineRows.Children.Add(row);
        }
    }

    private void Refresh()
    {
        var enabled = App.Current.Settings.SearchEngines;
        foreach (var (id, toggle) in _toggles)
            toggle.IsChecked = enabled.Any(e => string.Equals(e, id, StringComparison.OrdinalIgnoreCase));
    }

    private void OnEngineToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string id } clicked) return;

        // Keep the catalogue order so the result rows never reshuffle.
        var current = new HashSet<string>(App.Current.Settings.SearchEngines, StringComparer.OrdinalIgnoreCase);
        if (clicked.IsChecked == true) current.Add(id);
        else current.Remove(id);

        var ordered = SearchEngineCatalog.All.Where(x => current.Contains(x.Id)).Select(x => x.Id).ToList();
        App.Current.SetSearchEngines(ordered);
        Refresh();
    }

    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        App.Current.SetSearchEngines(new[] { "baidu", "google", "bing" });
        Refresh();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
