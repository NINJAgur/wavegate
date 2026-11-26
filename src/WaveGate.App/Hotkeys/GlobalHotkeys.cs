using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WaveGate.Overlay.Hotkeys;

/// <summary>
/// RegisterHotKey bound to a window handle. The handle is created up front so
/// hotkeys keep working while the overlay is hidden.
/// </summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = [];
    private int _nextId = 1;

    public GlobalHotkeys(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
    }

    /// <summary>Returns false if another application already owns the combination.</summary>
    public bool Register(Hotkey hotkey, Action action)
    {
        int id = _nextId++;
        // WPF ModifierKeys values match MOD_ALT/MOD_CONTROL/MOD_SHIFT/MOD_WIN.
        uint modifiers = (uint)hotkey.Modifiers | MOD_NOREPEAT;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);

        if (!RegisterHotKey(_source.Handle, id, modifiers, vk)) return false;
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
