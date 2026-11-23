using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopJumpList.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const int HotkeyId = 0x4A4D;

    private HwndSource? _source;
    private IntPtr _handle;
    private bool _registered;
    private Action? _callback;

    public bool IsRegistered => _registered;
    public string Gesture { get; private set; } = "Ctrl+Alt+J";

    public bool Register(IntPtr handle, string gesture, Action callback)
    {
        Unregister();
        _handle = handle;
        Gesture = gesture;
        _callback = callback;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        if (!TryParse(gesture, out var modifiers, out var key)) return false;
        _registered = RegisterHotKey(handle, HotkeyId, modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            _callback?.Invoke();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static bool TryParse(string gesture, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(gesture)) return false;

        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!Enum.TryParse(part, ignoreCase: true, out key) || key == Key.None) return false;
                    break;
            }
        }

        return key != Key.None;
    }

    public void Unregister()
    {
        if (_registered && _handle != IntPtr.Zero) UnregisterHotKey(_handle, HotkeyId);
        _registered = false;
        _source?.RemoveHook(WndProc);
        _source = null;
        _handle = IntPtr.Zero;
    }

    public void Dispose() => Unregister();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
