using System.Windows.Input;
using System.Windows.Interop;

namespace Tanjelo;

/// <summary>
/// A global hotkey that is only registered while File Explorer, the desktop or the Float shelf is in front, so the
/// same keys keep working in every other app (Ctrl+Shift+Space is parameter hints in VS Code, for example).
/// </summary>
sealed class HotkeyManager : IDisposable
{
    const int HotkeyId = 0xC1E, ShelfHotkeyId = 0xC1F;

    readonly HwndSource _window;
    readonly Native.WinEventProc _onForegroundChanged; // held so the GC can't collect the callback
    readonly IntPtr _hook;
    uint _modifiers, _key;
    bool _registered, _reportedConflict, _shelfRegistered;

    public event Action? Pressed;

    /// <summary>Raised once if another program already owns the key combination.</summary>
    public event Action? Conflict;

    /// <summary>The optional shortcut that shows or hides the Float shelf, which works everywhere.</summary>
    public event Action? ShelfPressed;

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

    /// <summary>Sets the shelf's shortcut, or removes it with null. Returns false if another program owns those keys.</summary>
    public bool SetShelf((ModifierKeys Modifiers, Key Key)? shortcut)
    {
        if (_shelfRegistered) Native.UnregisterHotKey(_window.Handle, ShelfHotkeyId);
        _shelfRegistered = false;
        if (shortcut is not { } keys) return true;
        _shelfRegistered = Native.RegisterHotKey(_window.Handle, ShelfHotkeyId, Modifiers(keys.Modifiers) | Native.MOD_NOREPEAT, (uint)KeyInterop.VirtualKeyFromKey(keys.Key));
        return _shelfRegistered;
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
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == ShelfHotkeyId)
        {
            handled = true;
            ShelfPressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        SetShelf(null);
        Native.UnhookWinEvent(_hook);
        _window.Dispose();
    }
}
