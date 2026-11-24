using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using DesktopJumpList.Infrastructure;
using DesktopJumpList.Models;
using DesktopJumpList.Services;

namespace DesktopJumpList.Views;

/// <summary>
/// The "/" palette: typing a slash turns the panel into a list of capabilities. Command rows put
/// their own text into the search box, result rows run when you press Enter.
/// </summary>
public partial class LauncherWindow
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ───────────────────────── Rows ─────────────────────────

    /// <summary>Renders the "/" palette into the grid, without touching the recently-used row.</summary>
    private void RebuildSlash(string query)
    {
        _insertMarker = null;
        _markerIndex = -1;
        _recentItems = new List<ShortcutItem>();

        var rows = BuildSlashRows(query).Select(ToResultRow).OfType<ResultRow>().ToList();
        Sync(_tiles, rows.Cast<object>().ToList());
        Sync(_recentTiles, new List<object>());

        RecentSection.Visibility = Visibility.Collapsed;
        Scroller.Margin = new Thickness(14, 14, 10, 14);
        EmptyState.Visibility = Visibility.Collapsed;
        Scroller.Visibility = Visibility.Visible;

        var none = rows.Count == 0;
        NoMatch.Visibility = none ? Visibility.Visible : Visibility.Collapsed;
        NoMatch.Text = none ? $"没有匹配的 / 命令：{query}" : string.Empty;
        ResultCount.Text = rows.Count > 0 ? rows.Count.ToString() : string.Empty;
        Tiles.SelectedIndex = rows.Count > 0 ? 0 : -1;

        Panel.Set(System.Windows.Controls.Border.HeightProperty, TargetPanelHeight());
        EnsureWindowFits();
    }

    /// <summary>Maps a shortcut / engine / palette row onto the one-line list model.</summary>
    private ResultRow? ToResultRow(object item) => item switch
    {
        ShortcutItem shortcut => BuildItemRow(shortcut),
        SearchResultTile search => new ResultRow
        {
            Title = search.Name,
            Subtitle = $"「{search.Query}」",
            Glyph = search.Engine.Monogram,
            Accent = search.Engine.BrandBrush,
            Run = () => OpenSearch(search)
        },
        SlashRow slash => new ResultRow
        {
            Title = slash.Title,
            Subtitle = slash.Subtitle,
            Glyph = slash.Glyph,
            Accent = slash.Accent,
            Fill = slash.Fill,
            Run = slash.Run
        },
        _ => null
    };

    /// <summary>An item row shows its real icon; without one it falls back to a lettered tile.</summary>
    private static ResultRow BuildItemRow(ShortcutItem item)
    {
        var row = new ResultRow
        {
            Item = item,
            Title = item.Name,
            Subtitle = RowSubtitle(item),
            Icon = item.Icon
        };

        if (item.Icon is null && item.Name.Length > 0)
        {
            row.Glyph = item.Name[..1];
            row.Accent = NameBrushConverter.Convert(item.Name, typeof(System.Windows.Media.Brush), null!, Inv) as System.Windows.Media.Brush;
        }

        return row;
    }

    private static readonly Infrastructure.NameToBrushConverter NameBrushConverter = new();

    private static string RowSubtitle(ShortcutItem item)
    {
        var where = item.Folder is { Length: > 0 } folder ? $"分组 · {folder}" : item.Target;
        return item.IsUnavailable ? $"目标已失效 · {where}" : where ?? string.Empty;
    }

    /// <summary>Enter or click on a result row: fill the box for a command, run a result, open an item.</summary>
    private void RunResultRow(ResultRow row)
    {
        if (row.Fill is { } fill)
        {
            Query.Text = fill;
            Query.CaretIndex = Query.Text.Length;
            Keyboard.Focus(Query);
            return;
        }

        if (row.Run is { } run) { run(); return; }
        if (row.Item is { } item) { Invoke(item, null); return; }
        ShowMessage("这一行只是用法提示，补上参数再回车");
    }

    /// <summary>Kept for the palette's own rows before they are wrapped.</summary>
    private void RunSlashRow(SlashRow row)
    {
        if (ToResultRow(row) is { } mapped) RunResultRow(mapped);
    }

    internal List<SlashRow> BuildSlashRows(string query)
    {
        var body = query.StartsWith('/') ? query[1..] : query;
        var trimmed = body.TrimStart();
        if (trimmed.Length == 0) return AllCommandRows();

        var space = trimmed.IndexOf(' ');
        var name = space < 0 ? trimmed : trimmed[..space];
        var args = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();

        var command = SlashCommandCatalog.Find(name);
        if (command is null)
        {
            var needle = trimmed.ToLowerInvariant();
            return AllCommandRows()
                .Where(r => r.Title.ToLowerInvariant().Contains(needle) || r.Subtitle.ToLowerInvariant().Contains(needle))
                .ToList();
        }

        if (args.Length == 0 && command.NeedsArgs) return new List<SlashRow> { CommandRow(command) };
        return Execute(command, args);
    }

    private static List<SlashRow> AllCommandRows() =>
        SlashCommandCatalog.All.Select(CommandRow).ToList();

    private static SlashRow CommandRow(SlashCommand command) =>
        new(command.Glyph, command.ColorHex, command.Title, command.Usage,
            isCommand: true, fill: "/" + command.Name + " ");

    private static SlashRow Result(string glyph, string color, string title, string subtitle, Action? run) =>
        new(glyph, color, title, subtitle, run: run);

    private List<SlashRow> Execute(SlashCommand command, string args)
    {
        switch (command.Name)
        {
            case "help": return AllCommandRows();
            case "calc": return CalcRows(args);
            case "unit": return UnitRows(args);
            case "s": return SearchRows(DefaultEngine(), args);
            case "add": return AddRow(args);
            case "pin": return PinRows(args, pin: true);
            case "unpin": return PinRows(args, pin: false);
            case "del": return DeleteRow(args);
            case "theme": return ThemeRows(args);
            case "layout": return LayoutRows(args);
            case "dock": return DockRows(args);
            case "settings":
                return One(Result("⚙", "#64748B", "打开设置", "回车打开设置窗口",
                    () => { Close(animated: true); App.Current.OpenSettings(); }));
            case "advanced":
                return One(Result("✦", "#64748B", "打开高级设置", "搜索引擎等",
                    () => { Close(animated: true); App.Current.OpenAdvancedSettings(); }));
            case "data":
                return One(Result("▤", "#64748B", "打开数据目录", "本机数据保存在 %LocalAppData%",
                    () => { App.Current.OpenDataFolder(); Close(animated: true); }));
            case "quit":
                return One(Result("退", "#EF4444", "退出 Jump", "托盘图标也会消失",
                    () => App.Current.Quit()));
        }

        if (SearchEngineCatalog.ById(command.Name) is { } engine) return SearchRows(engine, args);
        return One(Result("?", "#6B7280", "未知命令", command.Usage, null));
    }

    private static List<SlashRow> One(SlashRow row) => new() { row };

    // ───────────────────────── Individual capabilities ─────────────────────────

    private static SearchEngineOption DefaultEngine() =>
        SearchEngineCatalog.Resolve(App.Current.Settings.SearchEngines).FirstOrDefault()
        ?? SearchEngineCatalog.All[0];

    private List<SlashRow> SearchRows(SearchEngineOption engine, string query)
    {
        var url = engine.BuildUrl(query);
        return One(Result(engine.Monogram, engine.ColorHex, $"用{engine.Name}搜索",
            $"「{query}」", () =>
            {
                if (ShellLauncher.OpenUrl(url) is { } error) { ShowMessage(error); return; }
                Close(animated: true);
            }));
    }

    private List<SlashRow> CalcRows(string args)
    {
        if (!TryCalculate(args, out var value))
            return One(Result("=", "#4C8BF5", "无法计算", "只支持 + − × ÷ ^ 和括号，例如 /calc 12*(8+3)", null));

        var text = FormatNumber(value);
        return One(Result("=", "#4C8BF5", text, "回车复制", () =>
        {
            try { Clipboard.SetText(text); ShowMessage($"已复制 {text}"); }
            catch { ShowMessage("复制失败：剪贴板被占用"); }
        }));
    }

    private List<SlashRow> UnitRows(string args)
    {
        if (!TryConvertUnit(args, out var text, out var error))
            return One(Result("⇄", "#0EA5A5", "换算不了", error, null));

        return One(Result("⇄", "#0EA5A5", text, "回车复制", () =>
        {
            try { Clipboard.SetText(text); ShowMessage($"已复制 {text}"); }
            catch { ShowMessage("复制失败：剪贴板被占用"); }
        }));
    }

    private List<SlashRow> AddRow(string args)
    {
        var item = ShellLauncher.FromInput(args);
        if (item is null)
            return One(Result("+", "#22C55E", "无法识别", "给一个存在的路径或网址", null));

        return One(Result("+", "#22C55E", $"添加「{item.Name}」", item.Target, () =>
        {
            Commit(new List<ShortcutItem> { item });
            ShowMessage($"已添加「{item.Name}」");
        }));
    }

    private List<SlashRow> PinRows(string args, bool pin)
    {
        var item = FindItemByName(args);
        if (item is null)
            return One(Result("钉", "#F59E0B", "没找到这项", $"没有名称包含「{args}」的条目", null));

        var already = item.Pinned == pin;
        return One(Result("钉", "#F59E0B", pin ? $"固定「{item.Name}」" : $"取消固定「{item.Name}」",
            already ? "它已经是这个状态了" : "回车立即生效", () =>
            {
                item.Pinned = pin;
                _library.SaveSoon();
                RefreshOrder();
                ShowMessage(pin ? $"已固定「{item.Name}」" : $"已取消固定「{item.Name}」");
            }));
    }

    private List<SlashRow> DeleteRow(string args)
    {
        var item = FindItemByName(args);
        if (item is null)
            return One(Result("−", "#EF4444", "没找到这项", $"没有名称包含「{args}」的条目", null));

        return One(Result("−", "#EF4444", $"移除「{item.Name}」", "可用 Ctrl+Z 撤销", () =>
        {
            Remove(item);
            Close(animated: true);
        }));
    }

    private List<SlashRow> ThemeRows(string args)
    {
        var mode = args.ToLowerInvariant() switch
        {
            "dark" or "深色" or "黑" => AppTheme.Dark,
            "light" or "浅色" or "白" => AppTheme.Light,
            "auto" or "system" or "跟随" or "自动" => AppTheme.System,
            _ => (AppTheme?)null
        };
        if (mode is null)
            return One(Result("◐", "#8B5CF6", "选择主题", "/theme dark | light | auto", null));

        var name = mode == AppTheme.Dark ? "深色" : mode == AppTheme.Light ? "浅色" : "跟随系统";
        return One(Result("◐", "#8B5CF6", $"切换为{name}", "回车立即生效", () =>
        {
            App.Current.SetTheme(mode.Value);
            ShowMessage($"外观已切换为{name}");
        }));
    }

    private List<SlashRow> LayoutRows(string args)
    {
        var match = Regex.Match(args, @"^\s*(\d+)\s*[x*×\s]\s*(\d+)\s*$", RegexOptions.IgnoreCase);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var cols) || !int.TryParse(match.Groups[2].Value, out var rows))
            return One(Result("▦", "#0EA5A5", "布局参数", "写成 6x4，表示每行 6 个、最多 4 行", null));

        return One(Result("▦", "#0EA5A5", $"每行 {cols} · {rows} 行", "回车应用", () =>
        {
            App.Current.SetColumns(cols);
            App.Current.SetMaxRows(rows);
            ShowMessage($"布局已改为每行 {cols} 个、最多 {rows} 行");
        }));
    }

    private List<SlashRow> DockRows(string args)
    {
        var text = args.Trim().ToLowerInvariant();
        if (text is "hide" or "隐藏" or "off")
            return One(Result("●", "#10B981", "隐藏悬浮球", "仍可用快捷键和托盘打开", () =>
            {
                App.Current.SetShowDock(false);
                ShowMessage("已隐藏悬浮球");
            }));
        if (text is "show" or "显示" or "on")
            return One(Result("●", "#10B981", "显示悬浮球", "回车让它回到屏幕边缘", () =>
            {
                App.Current.SetShowDock(true);
                ShowMessage("已显示悬浮球");
            }));

        var size = Regex.Match(text, @"^size\s*=\s*(\d+)");
        if (size.Success && int.TryParse(size.Groups[1].Value, out var px))
            return One(Result("●", "#10B981", $"悬浮球大小 {px}px", "回车应用", () =>
            {
                App.Current.SetDockSize(px);
                ShowMessage($"悬浮球大小已设为 {px}px");
            }));

        var opacity = Regex.Match(text, @"^opacity\s*=\s*(\d+)");
        if (opacity.Success && int.TryParse(opacity.Groups[1].Value, out var percent))
            return One(Result("●", "#10B981", $"悬浮球不透明度 {percent}%", "回车应用", () =>
            {
                App.Current.SetDockOpacity(percent / 100.0);
                ShowMessage($"悬浮球不透明度已设为 {percent}%");
            }));

        return One(Result("●", "#10B981", "悬浮球", "/dock hide | show | size=52 | opacity=60", null));
    }

    private ShortcutItem? FindItemByName(string name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : _library.Items.FirstOrDefault(i => i.Name.Contains(name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    // ───────────────────────── Math ─────────────────────────

    private static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "—";
        if (Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15)
            return ((long)Math.Round(value)).ToString(Inv);
        return value.ToString("0.######", Inv);
    }

    private static bool TryCalculate(string expression, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        var parser = new ExpressionParser(expression.Replace('×', '*').Replace('÷', '/').Replace('−', '-'));
        return parser.TryParse(out value);
    }

    /// <summary>Recursive-descent evaluator: + − * / ^ and parentheses, plus a percent suffix.</summary>
    private sealed class ExpressionParser
    {
        private readonly string _text;
        private int _index;

        public ExpressionParser(string text) => _text = text;

        public bool TryParse(out double value)
        {
            value = 0;
            try
            {
                var result = ParseSum();
                SkipWhitespace();
                if (_index < _text.Length) return false;
                value = result;
                return !double.IsNaN(result) && !double.IsInfinity(result);
            }
            catch
            {
                return false;
            }
        }

        private void SkipWhitespace()
        {
            while (_index < _text.Length && char.IsWhiteSpace(_text[_index])) _index++;
        }

        private char Peek()
        {
            SkipWhitespace();
            return _index < _text.Length ? _text[_index] : '\0';
        }

        private double ParseSum()
        {
            var value = ParseProduct();
            while (true)
            {
                var c = Peek();
                if (c == '+') { _index++; value += ParseProduct(); }
                else if (c == '-') { _index++; value -= ParseProduct(); }
                else return value;
            }
        }

        private double ParseProduct()
        {
            var value = ParseUnary();
            while (true)
            {
                var c = Peek();
                if (c == '*') { _index++; value *= ParseUnary(); }
                else if (c == '/')
                {
                    _index++;
                    var divisor = ParseUnary();
                    if (Math.Abs(divisor) < double.Epsilon) throw new DivideByZeroException();
                    value /= divisor;
                }
                else return value;
            }
        }

        private double ParseUnary()
        {
            var c = Peek();
            if (c == '-') { _index++; return -ParseUnary(); }
            if (c == '+') { _index++; return ParseUnary(); }
            return ParsePower();
        }

        private double ParsePower()
        {
            var value = ParseAtom();
            if (Peek() != '^') return value;
            _index++;
            return Math.Pow(value, ParseUnary());
        }

        private double ParseAtom()
        {
            SkipWhitespace();
            if (Peek() == '(')
            {
                _index++;
                var inner = ParseSum();
                if (Peek() != ')') throw new FormatException();
                _index++;
                return Postfix(inner);
            }

            var start = _index;
            while (_index < _text.Length && (char.IsDigit(_text[_index]) || _text[_index] == '.')) _index++;
            if (start == _index) throw new FormatException();
            return Postfix(double.Parse(_text[start.._index], Inv));
        }

        private double Postfix(double value)
        {
            SkipWhitespace();
            while (_index < _text.Length && _text[_index] == '%')
            {
                _index++;
                value /= 100;
                SkipWhitespace();
            }
            return value;
        }
    }

    // ───────────────────────── Units ─────────────────────────

    private static readonly (string[] Names, double Factor)[] LengthUnits =
    {
        (new[] { "mm", "毫米" }, 1),
        (new[] { "cm", "厘米" }, 10),
        (new[] { "m", "米" }, 1000),
        (new[] { "km", "千米", "公里" }, 1_000_000),
        (new[] { "in", "inch", "英寸" }, 25.4),
        (new[] { "ft", "foot", "feet", "英尺" }, 304.8),
        (new[] { "yd", "码" }, 914.4),
        (new[] { "mi", "mile", "英里" }, 1_609_344),
    };

    private static readonly (string[] Names, double Factor)[] MassUnits =
    {
        (new[] { "mg", "毫克" }, 0.001),
        (new[] { "g", "克" }, 1),
        (new[] { "kg", "千克", "公斤" }, 1000),
        (new[] { "t", "吨" }, 1_000_000),
        (new[] { "lb", "pound", "磅" }, 453.59237),
        (new[] { "oz", "盎司" }, 28.349523125),
    };

    private static readonly (string[] Names, double Factor)[] DataUnits =
    {
        (new[] { "b", "byte", "字节" }, 1),
        (new[] { "kb", "k" }, 1024),
        (new[] { "mb", "m" }, 1024.0 * 1024),
        (new[] { "gb", "g" }, 1024.0 * 1024 * 1024),
        (new[] { "tb", "t" }, 1024.0 * 1024 * 1024 * 1024),
    };

    private static readonly (string[] Names, double Factor)[] TimeUnits =
    {
        (new[] { "ms", "毫秒" }, 0.001),
        (new[] { "s", "sec", "秒" }, 1),
        (new[] { "min", "分", "分钟" }, 60),
        (new[] { "h", "hour", "小时" }, 3600),
        (new[] { "d", "day", "天" }, 86400),
    };

    private static bool TryConvertUnit(string args, out string text, out string error)
    {
        text = string.Empty;
        error = "写成 10cm in inch / 5kg to 磅 / 100f in c";

        var match = Regex.Match(args,
            @"^\s*(-?[\d.]+)\s*([a-zA-Z\u4e00-\u9fa5]+)\s*(?:in|to|>|→|=|＝)\s*([a-zA-Z\u4e00-\u9fa5]+)\s*$",
            RegexOptions.IgnoreCase);
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, Inv, out var value)) return false;

        var from = match.Groups[2].Value;
        var to = match.Groups[3].Value;

        if (TryTemperature(from, to, value) is { } temperature)
        {
            text = $"{FormatNumber(value)} {from} = {FormatNumber(temperature)} {to}";
            return true;
        }

        foreach (var table in new[] { LengthUnits, MassUnits, DataUnits, TimeUnits })
        {
            var a = FindUnit(table, from);
            var b = FindUnit(table, to);
            if (a is null || b is null) continue;
            var converted = value * a.Value / b.Value;
            text = $"{FormatNumber(value)} {from} = {FormatNumber(converted)} {to}";
            return true;
        }

        error = $"「{from}」和「{to}」不是同一类单位（支持长度 / 重量 / 数据 / 时间 / 温度）";
        return false;
    }

    private static double? FindUnit((string[] Names, double Factor)[] table, string name) =>
        table.FirstOrDefault(u => u.Names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) is { Factor: > 0 } hit
            ? hit.Factor
            : null;

    private static double? TryTemperature(string from, string to, double value)
    {
        double? ToCelsius(string unit) => unit.ToLowerInvariant() switch
        {
            "c" or "°c" or "摄氏" or "摄氏度" => value,
            "f" or "°f" or "华氏" or "华氏度" => (value - 32) * 5 / 9,
            "k" or "开" or "开尔文" => value - 273.15,
            _ => null
        };

        var celsius = ToCelsius(from);
        if (celsius is null) return null;

        return to.ToLowerInvariant() switch
        {
            "c" or "°c" or "摄氏" or "摄氏度" => celsius,
            "f" or "°f" or "华氏" or "华氏度" => celsius * 9 / 5 + 32,
            "k" or "开" or "开尔文" => celsius + 273.15,
            _ => null
        };
    }
}
