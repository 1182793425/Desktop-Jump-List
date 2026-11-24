using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopJumpList.Services;
using Microsoft.Win32;

namespace DesktopJumpList.Views;

public partial class StoreAppPicker : Window
{
    private readonly List<StoreAppsService.StoreApp> _all;
    private ImageBrush? _glassBrush;

    public StoreAppsService.StoreApp? Selected { get; private set; }

    public StoreAppPicker(List<StoreAppsService.StoreApp> apps)
    {
        InitializeComponent();
        _all = apps;
        Apply(filter: string.Empty);

        Search.TextChanged += (_, _) => Apply(Search.Text.Trim());
        Search.PreviewKeyDown += OnSearchKey;
        List.MouseDoubleClick += (_, _) => Accept();
        List.PreviewKeyDown += OnListKey;

        // The filter is the primary input; clicking a row should not steal typing from it.
        List.PreviewMouseLeftButtonUp += (_, _) => Search.Focus();

        // Stay invisible for the first frame so the glass capture sees the desktop/owner,
        // not the picker itself.
        Opacity = 0;
        Loaded += (_, _) =>
        {
            Search.Focus();
            Keyboard.Focus(Search);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                UpdateLayout();
                _glassBrush = App.Current.Settings.FrostedGlass
                    ? GlassBackdrop.Capture(this, PanelBorder, 20)
                    : null;
                ApplyGlass();
                Opacity = 1;
            });
        };
    }

    private void ApplyGlass()
    {
        var on = App.Current.Settings.FrostedGlass && _glassBrush is not null;
        if (on)
        {
            GlassLayer.Background = _glassBrush;
        }
        GlassLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        TintLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Apply(string filter)
    {
        var items = string.IsNullOrEmpty(filter)
            ? _all
            : _all.Where(a => a.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        List.ItemsSource = items;
        NoMatch.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        List.SelectedIndex = items.Count > 0 ? 0 : -1;
        if (items.Count > 0) List.ScrollIntoView(items[0]);
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
        }
    }

    private void OnListKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (List.Items.Count == 0) return;
        var index = List.SelectedIndex;
        var next = index < 0 ? 0 : Math.Clamp(index + delta, 0, List.Items.Count - 1);
        List.SelectedIndex = next;
        if (List.ItemContainerGenerator.ContainerFromIndex(next) is FrameworkElement container)
            container.BringIntoView();
    }

    private void Accept()
    {
        var app = List.SelectedItem as StoreAppsService.StoreApp
                  ?? List.Items.OfType<StoreAppsService.StoreApp>().FirstOrDefault();
        if (app is null) return;
        Selected = app;
        DialogResult = true;
    }
}
