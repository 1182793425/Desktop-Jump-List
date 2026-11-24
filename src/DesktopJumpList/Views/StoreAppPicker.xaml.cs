using System.Windows;
using System.Windows.Input;
using DesktopJumpList.Services;
using Microsoft.Win32;

namespace DesktopJumpList.Views;

public partial class StoreAppPicker : Window
{
    private readonly List<StoreAppsService.StoreApp> _all;

    public StoreAppsService.StoreApp? Selected { get; private set; }

    public StoreAppPicker(List<StoreAppsService.StoreApp> apps)
    {
        InitializeComponent();
        _all = apps;
        Apply(filter: string.Empty);

        Search.TextChanged += (_, _) => Apply(Search.Text.Trim());
        Search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
            else if (e.Key == Key.Down && List.Items.Count > 0) { List.SelectedIndex = 0; e.Handled = true; }
        };
        List.MouseDoubleClick += (_, _) => Accept();
        List.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Accept(); e.Handled = true; }
            else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
        };

        Loaded += (_, _) => { Search.Focus(); Keyboard.Focus(Search); };
    }

    private void Apply(string filter)
    {
        var items = string.IsNullOrEmpty(filter)
            ? _all
            : _all.Where(a => a.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        List.ItemsSource = items;
        if (items.Count > 0) List.SelectedIndex = 0;
    }

    private void Accept()
    {
        if (List.SelectedItem is StoreAppsService.StoreApp app)
        {
            Selected = app;
            DialogResult = true;
        }
    }
}
