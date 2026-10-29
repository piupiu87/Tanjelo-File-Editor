using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Tanjelo;

/// <summary>
/// A box that records a keyboard shortcut: focus it and press the keys. Tab still moves on and Escape still closes the
/// window; Backspace or Delete clears an optional shortcut.
/// </summary>
sealed class ShortcutBox : TextBox
{
    string? _shortcut;
    bool _hinting; // showing a hint of its own, to clear on leaving

    /// <summary>Raised with the new shortcut, or null when an optional one is cleared.</summary>
    public event Action<string?>? Recorded;

    /// <summary>Raised when the box starts and stops listening, so the app can let go of its own shortcuts meanwhile.</summary>
    public event Action<bool>? Listening;

    /// <summary>Raised with a hint about the keys pressed, such as that they need Ctrl, Alt or Win; empty to clear it.</summary>
    public event Action<string>? Hint;

    /// <summary>Whether the shortcut can be cleared, leaving none.</summary>
    public bool Optional { get; init; }

    public ShortcutBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        ContextMenu = null;
        Cursor = Cursors.Arrow;
    }

    public string? Shortcut
    {
        get => _shortcut;
        set
        {
            _shortcut = string.IsNullOrWhiteSpace(value) ? null : value;
            Text = _shortcut ?? "";
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Press(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Letting go of the modifiers without a key puts back the shortcut.
        if (Keyboard.Modifiers == ModifierKeys.None) Text = _shortcut ?? "";
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Listening?.Invoke(true);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Text = _shortcut ?? "";
        ShowHint("");
        Listening?.Invoke(false);
    }

    /// <summary>A key going down with these modifiers held; returns whether the box used it.</summary>
    internal bool Press(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Tab when (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None:
            case Key.Escape when modifiers == ModifierKeys.None:
                return false;
            case Key.Back or Key.Delete when modifiers == ModifierKeys.None:
                if (Optional) Record(null);
                return true;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed:
                Text = Settings.FormatModifiers(modifiers);
                return true;
        }
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == ModifierKeys.None)
        {
            ShowHint("Hold Ctrl, Alt or Win with the key, so the shortcut doesn't get in the way of typing.");
            return true;
        }
        Record(Settings.FormatHotkey(modifiers, key));
        return true;
    }

    void Record(string? shortcut)
    {
        Shortcut = shortcut;
        ShowHint("");
        Recorded?.Invoke(_shortcut);
    }

    void ShowHint(string hint)
    {
        if (hint.Length == 0 && !_hinting) return;
        _hinting = hint.Length > 0;
        Hint?.Invoke(hint);
    }
}
