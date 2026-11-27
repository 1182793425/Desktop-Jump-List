using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopJumpList.Services;

/// <summary>
/// Hidden `--icon-selftest` mode: prints, for every registry app and every shell:AppsFolder
/// entry, whether the icon pipeline produces an image. Development aid only.
/// </summary>
public static class IconSelfTest
{
    public static void Run()
    {
        var apps = new AppIndex();
        apps.Rebuild();

        Console.WriteLine($"── registry apps ({apps.Count}) ──");
        foreach (var app in apps.Snapshot())
        {
            var icon = IconService.Get(app.ExePath);
            Console.WriteLine($"[{(icon is null ? "MISS" : "ok  ")}] {Size(icon),-9} {app.Name} -> {app.ExePath}");
        }

        Console.WriteLine();
        Console.WriteLine("── apps folder ──");
        foreach (var app in AppsFolderService.Enumerate())
        {
            var path = "shell:AppsFolder\\" + app.AumId;
            var icon = IconService.Get(path);
            Console.WriteLine($"[{(icon is null ? "MISS" : "ok  ")}] {Size(icon),-9} {app.Name} -> {app.AumId}");
        }
    }

    private static string Size(ImageSource? image) =>
        image is BitmapSource bmp ? $"{bmp.PixelWidth}x{bmp.PixelHeight}" : "-";
}
