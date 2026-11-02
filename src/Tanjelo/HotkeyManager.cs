using System.Windows.Input;
using System.Windows.Interop;

namespace Tanjelo;

/// <summary>
/// A global hotkey that is only registered while File Explorer, the desktop or the Float shelf is in front, so the
/// same keys keep working in every other app (Ctrl+Shift+Space is parameter hints in VS Code, for example).
/// </summary>
sealed class HotkeyManager : IDisposable
{
    const int HotkeyId = 0xC1E;

    /// <summary>The optional shortcuts that work in every app, each with its own hotkey number.</summary>
    public enum Global { Shelf = 0xC1F, Clipboard = 0xC20 }

    readonly HwndSource _window;
    readonly Native.WinEventProc _onForegroundChanged; // held so the GC can't collect the callback
    readonly IntPtr _hook;
    readonly HashSet<Global> _globals = [];
    uint _modifiers, _key;
    bool _registered, _reportedConflict;

    public event Action? Pressed;

    /// <summary>Raised once if another program already owns the key combination.</summary>
    public event Action? Conflict;

    /// <summary>The optional shortcut that shows or hides the Float shelf, which works everywhere.</summary>
    public event Action? ShelfPressed;

    /// <summary>The optional shortcut that opens the menu for the clipboard picture, which works everywhere.</summary>
    public event Action? ClipboardPressed;

    /// <summary>Other windows the shortcut works in besides File Explorer and the desktop, such as the Float shelf.</summary>
    public Func<IntPtr, bool>? AlsoIn { get; set; }

    public HotkeyManager()
    {
        _window = new HwndSource(new HwndSourceParameters("Tanjelo hotkey") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _window.AddHook(WndProc);
        _onForegroundChanged = (_, _, hwnd, _, _, _, _) => Update(hwnd);
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            _onForegroundChanged, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    public void Set(ModifierKeys modifiers, Key key)
    {
        Unregister();
        _modifiers = Modifiers(modifiers);
        _key = (uint)KeyInterop.VirtualKeyFromKey(key);
        _reportedConflict = false;
        Update(Native.GetForegroundWindow());
    }

    /// <summary>Sets one of the shortcuts that work everywhere, or removes it with null. Returns false if another program owns those keys.</summary>
    public bool SetGlobal(Global which, (ModifierKeys Modifiers, Key Key)? shortcut)
    {
        if (_globals.Remove(which)) Native.UnregisterHotKey(_window.Handle, (int)which);
        if (shortcut is not { } keys) return true;
        var registered = Native.RegisterHotKey(_window.Handle, (int)which, Modifiers(keys.Modifiers) | Native.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(keys.Key));
        if (registered) _globals.Add(which);
        return registered;
    }

    static uint Modifiers(ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Control) ? Native.MOD_CONTROL : 0)
        | (modifiers.HasFlag(ModifierKeys.Shift) ? Native.MOD_SHIFT : 0)
        | (modifiers.HasFlag(ModifierKeys.Alt) ? Native.MOD_ALT : 0)
        | (modifiers.HasFlag(ModifierKeys.Windows) ? Native.MOD_WIN : 0);

    void Update(IntPtr foreground)
    {
        if (_key == 0) return;
        var wanted = ExplorerSelection.IsFileWindow(foreground) || AlsoIn?.Invoke(foreground) == true;
        if (wanted && !_registered)
        {
            _registered = Native.RegisterHotKey(_window.Handle, HotkeyId, _modifiers | Native.MOD_NOREPEAT, _key);
            if (!_registered && !_reportedConflict)
            {
                _reportedConflict = true;
                Conflict?.Invoke();
            }
        }
        else if (!wanted && _registered)
        {
            Unregister();
        }
    }

    void Unregister()
    {
        if (!_registered) return;
        Native.UnregisterHotKey(_window.Handle, HotkeyId);
        _registered = false;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == (int)Global.Shelf)
        {
            handled = true;
            ShelfPressed?.Invoke();
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == (int)Global.Clipboard)
        {
            handled = true;
            ClipboardPressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        foreach (var which in _globals.ToList()) SetGlobal(which, null);
        Native.UnhookWinEvent(_hook);
        _window.Dispose();
    }
}
