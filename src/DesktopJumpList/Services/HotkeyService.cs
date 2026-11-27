using System.Windows.Input;
using System.Windows.Interop;
using DesktopJumpList.Infrastructure;

namespace DesktopJumpList.Services;

/// <summary>System-wide hotkey via RegisterHotKey on a message-only HwndSource.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int Id = 0x4A4D; // "JM"
    private readonly HwndSource _source;
    private bool _registered;

    public event Action? Pressed;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("JumpHotkey") { WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    public bool Register(string text)
    {
        Unregister();
        if (!TryParse(text, out var mods, out var vk)) return false;
        _registered = Native.RegisterHotKey(_source.Handle, Id, mods | Native.MOD_NOREPEAT, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered) Native.UnregisterHotKey(_source.Handle, Id);
        _registered = false;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == Id)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= Native.MOD_CONTROL; break;
                case "alt": mods |= Native.MOD_ALT; break;
                case "shift": mods |= Native.MOD_SHIFT; break;
                case "win": mods |= Native.MOD_WIN; break;
                default:
                    var conv = new KeyConverter();
                    if (conv.ConvertFromInvariantString(raw) is Key key)
                        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    else return false;
                    break;
            }
        }
        return vk != 0 && (mods != 0 || AllowsStandalone(vk));
    }

    /// <summary>
    /// Keys that are safe to grab without a modifier: function keys, the navigation cluster,
    /// media/volume keys and the numpad. Letters, digits, Space, Tab, Enter and the arrows are
    /// deliberately excluded — a global hotkey on those would swallow normal typing everywhere.
    /// </summary>
    public static bool AllowsStandalone(uint vk) => vk is
        >= 0x70 and <= 0x87        // F1..F24
        or 0x2C or 0x91 or 0x13    // PrintScreen, ScrollLock, Pause
        or 0x21 or 0x22 or 0x23    // PageUp, PageDown, End
        or 0x24 or 0x2D or 0x2E    // Home, Insert, Delete
        or 0x60 or 0x61 or 0x62    // numpad 0..2
        or 0x63 or 0x64 or 0x65    // numpad 3..5
        or 0x66 or 0x67 or 0x68    // numpad 6..8
        or 0x69 or 0x6A or 0x6B    // numpad 9, *, +
        or 0x6D or 0x6E or 0x6F    // numpad -, ., /
        or 0xAD or 0xAE or 0xAF    // mute, vol-, vol+
        or 0xB0 or 0xB1 or 0xB2    // next, prev, stop
        or 0xB3 or 0xB4 or 0xB5    // play/pause, mail, media
        or 0xB6 or 0xB7;           // app1, app2

    /// <summary>Human-readable reason a key alone is refused, or null when it is allowed.</summary>
    public static string? ExplainStandaloneRefusal(Key key) => key switch
    {
        Key.Tab or Key.Enter or Key.Escape or Key.Space or Key.Back =>
            "Tab / Enter / Esc / 空格 / 退格 会被其他程序使用，请至少加一个修饰键",
        >= Key.Left and <= Key.Down =>
            "单独的方向键会抢走光标移动，请加 Ctrl / Alt / Shift / Win",
        _ when key is >= Key.D0 and <= Key.D9 or >= Key.A and <= Key.Z =>
            "单独的字母或数字会让你无法正常打字，请加修饰键",
        _ => "这个键单独注册会被系统或其他程序占用，请加一个修饰键"
    };

    /// <summary>The stored spelling of a hotkey uses key-enum names ("Ctrl+Oem3"); show it as keys
    /// read on a keyboard ("Ctrl + `"). Storage stays untouched, this is display only.</summary>
    public static string FormatForDisplay(string? hotkey) =>
        string.Join(" + ", ParseParts(hotkey).Select(PrettyKey));

    public static IReadOnlyList<string> ParseParts(string? hotkey) =>
        string.IsNullOrWhiteSpace(hotkey)
            ? Array.Empty<string>()
            : hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>One key, printed the way it looks on a keycap.</summary>
    public static string PrettyKey(string part) => part.ToLowerInvariant() switch
    {
        "ctrl" or "control" or "leftctrl" or "rightctrl" => "Ctrl",
        "alt" or "leftalt" or "rightalt" => "Alt",
        "shift" or "leftshift" or "rightshift" => "Shift",
        "win" or "lwin" or "rwin" => "Win",
        "oem3" => "`",
        "oem1" => ";",
        "oem2" => "/",
        "oem4" => "[",
        "oem5" => @"\",
        "oem6" => "]",
        "oem7" => "'",
        "oem8" => "\\",
        "oemminus" => "-",
        "oemplus" => "=",
        "oemcomma" => ",",
        "oemperiod" => ".",
        "oemquestion" => "/",
        "oemtild" or "oemtilde" => "`",
        "space" => "空格",
        "escape" => "Esc",
        "return" => "Enter",
        "back" => "退格",
        "capital" => "Caps",
        "next" => "PgDn",
        "prior" => "PgUp",
        "snapshot" => "PrtSc",
        _ when part.Length == 2 && (part[0] is 'D' or 'd') && char.IsDigit(part[1]) => part[1..],
        _ => part
    };

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(Hook);
        _source.Dispose();
    }
}
