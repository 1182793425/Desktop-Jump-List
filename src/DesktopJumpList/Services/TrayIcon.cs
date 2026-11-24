using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using DesktopJumpList.Models;
using Forms = System.Windows.Forms;

namespace DesktopJumpList.Services;

/// <summary>
/// The permanent home of Jump: a notification-area icon. The app deliberately has no
/// taskbar button - this tray icon is how it is found again, opened, configured or quit.
/// The menu is rebuilt every time it opens so it always reflects the live settings.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private Icon? _ownedIcon;

    public TrayIcon(App app)
    {
        _app = app;

        _menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
            DropShadowEnabled = true,
            Font = new Font("Segoe UI", 9f)
        };
        _menu.Opening += (_, _) => BuildMenu();
        _menu.SizeChanged += (_, _) => RoundMenu();

        _ownedIcon = LoadIcon();
        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon,
            Text = "Jump · 快捷启动",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _icon.MouseClick += OnMouseClick;
    }

    private void OnMouseClick(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) _app.ShowLauncher();
    }

    /// <summary>A transient notification for errors that happen while the panel is closed.</summary>
    public void ShowBalloon(string text)
    {
        try { _icon.ShowBalloonTip(3500, "Jump", text, Forms.ToolTipIcon.Info); }
        catch { }
    }

    /// <summary>Developer preview: renders the tray menu to a PNG for visual inspection.</summary>
    internal void RenderPreview(string path)
    {
        BuildMenu();
        _menu.Show(new Point(120, 120));
        System.Windows.Forms.Application.DoEvents();
        _menu.Refresh();
        using var bitmap = new Bitmap(Math.Max(1, _menu.Width), Math.Max(1, _menu.Height));
        _menu.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        _menu.Close();
    }

    // ───────────────────────── Menu ─────────────────────────

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var dark = ThemeService.IsDark;
        _menu.Renderer = new MenuRenderer(dark);
        ToolStripManager.Renderer = _menu.Renderer; // submenus inherit the same colours
        _menu.ForeColor = dark ? Color.FromArgb(0xF2, 0xF4, 0xF8) : Color.FromArgb(0x1A, 0x1D, 0x24);
        _menu.BackColor = dark ? Color.FromArgb(0x23, 0x26, 0x2E) : Color.White;

        var s = _app.Settings;

        var open = Item("打开面板", _app.ShowLauncher);
        open.Font = new Font(_menu.Font, FontStyle.Bold);
        open.ShortcutKeyDisplayString = s.Hotkey;
        open.ShowShortcutKeys = true;
        _menu.Items.Add(open);
        _menu.Items.Add(Item("设置…", _app.OpenSettings));
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var theme = new Forms.ToolStripMenuItem("外观");
        theme.DropDownItems.Add(Check("跟随系统", s.Theme == AppTheme.System, () => _app.SetTheme(AppTheme.System)));
        theme.DropDownItems.Add(Check("浅色", s.Theme == AppTheme.Light, () => _app.SetTheme(AppTheme.Light)));
        theme.DropDownItems.Add(Check("深色", s.Theme == AppTheme.Dark, () => _app.SetTheme(AppTheme.Dark)));
        _menu.Items.Add(theme);

        var layout = new Forms.ToolStripMenuItem("布局");
        var cols = new Forms.ToolStripMenuItem("每行数量");
        foreach (var n in new[] { 3, 4, 5, 6, 7 })
        {
            var value = n;
            cols.DropDownItems.Add(Check($"{n} 个", s.Columns == n, () => _app.SetColumns(value)));
        }
        var rows = new Forms.ToolStripMenuItem("最大行数");
        foreach (var n in new[] { 2, 3, 4, 5, 6 })
        {
            var value = n;
            rows.DropDownItems.Add(Check($"{n} 行", s.MaxRows == n, () => _app.SetMaxRows(value)));
        }
        layout.DropDownItems.Add(cols);
        layout.DropDownItems.Add(rows);
        _menu.Items.Add(layout);

        _menu.Items.Add(new Forms.ToolStripSeparator());
        var startup = new Forms.ToolStripMenuItem("开机自动启动")
        {
            Checked = StartupService.IsEnabled,
            CheckOnClick = false
        };
        startup.Click += (_, _) =>
        {
            var want = !StartupService.IsEnabled;
            var error = _app.SetAutostart(want);
            if (error is not null) _app.Notify($"无法修改开机启动：{error}");
        };
        _menu.Items.Add(startup);
        _menu.Items.Add(Item("打开数据目录", _app.OpenDataFolder));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        // Quit through the dispatcher: disposing this menu from inside its own click can
        // leave the WinForms popup in a half-torn-down state.
        _menu.Items.Add(Item("退出 Jump", () => _app.Dispatcher.BeginInvoke(new Action(_app.Quit))));
    }

    private static Forms.ToolStripMenuItem Item(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static Forms.ToolStripMenuItem Check(string text, bool isChecked, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text) { Checked = isChecked, CheckOnClick = false };
        item.Click += (_, _) => action();
        return item;
    }

    private void RoundMenu()
    {
        if (_menu.Width <= 0 || _menu.Height <= 0) return;
        using var path = RoundRect(new Rectangle(0, 0, _menu.Width, _menu.Height), 10);
        _menu.Region?.Dispose();
        _menu.Region = new Region(path);
    }

    private static GraphicsPath RoundRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ───────────────────────── Icon ─────────────────────────

    private static Icon LoadIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (res is not null) return new Icon(res.Stream, new Size(32, 32));
        }
        catch { }
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } extracted)
                return extracted;
        }
        catch { }
        return (Icon)SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.MouseClick -= OnMouseClick;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
        _ownedIcon = null;
    }

    // ───────────────────────── Theme-aware renderer ─────────────────────────

    private sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color DarkInk = Color.FromArgb(0xF2, 0xF4, 0xF8);
        private static readonly Color LightInk = Color.FromArgb(0x1A, 0x1D, 0x24);
        private static readonly Color DarkFaint = Color.FromArgb(0x82, 0x8A, 0x99);
        private static readonly Color LightFaint = Color.FromArgb(0x8F, 0x97, 0xA3);
        private static readonly Color DarkAccent = Color.FromArgb(0x8A, 0xB0, 0xFF);
        private static readonly Color LightAccent = Color.FromArgb(0x3B, 0x6C, 0xF6);

        private readonly bool _dark;

        public MenuRenderer(bool dark) : base(new MenuColors(dark))
        {
            _dark = dark;
            RoundedEdges = true;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? (_dark ? DarkFaint : LightFaint) : (_dark ? DarkInk : LightInk);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            if (r.IsEmpty) { base.OnRenderItemCheck(e); return; }
            var g = e.Graphics;
            var previous = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var pen = new Pen(_dark ? DarkAccent : LightAccent, 1.9f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var cx = r.Left + r.Width / 2f;
            var cy = r.Top + r.Height / 2f;
            g.DrawLines(pen, new[]
            {
                new PointF(cx - 4.2f, cy),
                new PointF(cx - 1.2f, cy + 3.0f),
                new PointF(cx + 4.6f, cy - 3.6f)
            });
            g.SmoothingMode = previous;
        }
    }

    private sealed class MenuColors : ProfessionalColorTable
    {
        private readonly bool _dark;
        public MenuColors(bool dark) { _dark = dark; UseSystemColors = false; }

        private Color Surface => _dark ? Color.FromArgb(0x23, 0x26, 0x2E) : Color.FromArgb(0xFC, 0xFC, 0xFE);
        private Color Border => _dark ? Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x18, 0x00, 0x00, 0x00);
        private Color Hover => _dark ? Color.FromArgb(0x1E, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0x00, 0x00, 0x00);

        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color CheckBackground => Color.Transparent;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color StatusStripGradientBegin => Surface;
        public override Color StatusStripGradientEnd => Surface;
    }
}
