using System.IO;
using DesktopJumpList.Models;

namespace DesktopJumpList.Services;

/// <summary>Deterministic demo data used by --preview / --shot so screenshots never touch real user data.</summary>
public static class PreviewData
{
    public static List<ShortcutItem> CreateShortcuts()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var now = DateTimeOffset.Now;

        return
        [
            new ShortcutItem
            {
                Name = "项目工作区", Target = Directory.Exists(@"D:\code3\desktop-jump-list") ? @"D:\code3\desktop-jump-list" : home,
                Type = ShortcutType.Folder, Group = "开发", SortOrder = 0, IsPinned = true, OpenCount = 32,
                LastOpenedAt = now.AddMinutes(-12)
            },
            new ShortcutItem
            {
                Name = "下载", Target = Path.Combine(home, "Downloads"), Type = ShortcutType.Folder, Group = "常用",
                SortOrder = 1, OpenCount = 21, LastOpenedAt = now.AddHours(-2)
            },
            new ShortcutItem
            {
                Name = "记事本", Target = Path.Combine(system, "notepad.exe"), Type = ShortcutType.Application, Group = "常用",
                SortOrder = 2, OpenCount = 18, LastOpenedAt = now.AddHours(-5)
            },
            new ShortcutItem
            {
                Name = "GitHub", Target = "https://github.com", Type = ShortcutType.Url, Group = "开发",
                SortOrder = 3, OpenCount = 19, LastOpenedAt = now.AddHours(-9)
            },
            new ShortcutItem
            {
                Name = "素材库", Target = Path.Combine(home, "Pictures"), Type = ShortcutType.Folder, Group = "设计",
                SortOrder = 4, OpenCount = 14, LastOpenedAt = now.AddDays(-1)
            },
            new ShortcutItem
            {
                Name = "截图工具", Target = Path.Combine(system, "SnippingTool.exe"), Type = ShortcutType.Application,
                Group = "工作", SortOrder = 5, OpenCount = 12, LastOpenedAt = now.AddDays(-1)
            },
            new ShortcutItem
            {
                Name = "Figma", Target = "https://www.figma.com", Type = ShortcutType.Url, Group = "设计",
                SortOrder = 6, OpenCount = 11, LastOpenedAt = now.AddDays(-2)
            },
            new ShortcutItem
            {
                Name = "画图", Target = Path.Combine(system, "mspaint.exe"), Type = ShortcutType.Application, Group = "设计",
                SortOrder = 7, OpenCount = 7, LastOpenedAt = now.AddDays(-2)
            },
            new ShortcutItem
            {
                Name = "Dribbble", Target = "https://dribbble.com", Type = ShortcutType.Url, Group = "灵感",
                SortOrder = 8, OpenCount = 6, LastOpenedAt = now.AddDays(-3)
            },
            new ShortcutItem
            {
                Name = "设计说明", Target = Path.Combine(Directory.GetCurrentDirectory(), "README.md"), Type = ShortcutType.File,
                Group = "工作", SortOrder = 9, OpenCount = 4, LastOpenedAt = now.AddDays(-4)
            }
        ];
    }

    public static UsageData CreateUsage()
    {
        var usage = new UsageData();
        var values = new[] { 9, 14, 7, 17, 12, 19, 15 };
        var today = DateTime.Today;
        for (var i = 0; i < values.Length; i++)
        {
            var date = today.AddDays(i - (values.Length - 1));
            for (var n = 0; n < values[i]; n++) usage.Record(date);
        }

        return usage;
    }

    public static void EnsureSeed(ShortcutStore store, UsageStore usageStore, bool force)
    {
        var file = store.FilePath;
        if (!force && File.Exists(file)) return;
        Task.Run(async () =>
        {
            await store.SaveAsync(CreateShortcuts());
            await usageStore.SaveAsync(CreateUsage());
        }).GetAwaiter().GetResult();
    }
}
