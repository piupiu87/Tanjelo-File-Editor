using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Tanjelo.Core;

namespace Tanjelo;

/// <summary>
/// Hears each change to the clipboard, on a window of its own that's never shown, and raises <see cref="Screenshot"/>
/// for screenshots once the clipboard has been quiet for a moment. For anything else only the program that copied it,
/// and for a possible screenshot the list of formats, are looked at; <see cref="ScreenshotWatcher"/> decides.
/// </summary>
sealed class ClipboardWatcher : IDisposable
{
    readonly HwndSource _window;
    readonly DispatcherTimer _quiet = new() { Interval = TimeSpan.FromMilliseconds(250) };
    bool _listening;

    public ClipboardWatcher()
    {
        _window = new HwndSource(new HwndSourceParameters("Tanjelo clipboard") { ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ });
        _window.AddHook(WndProc);
        _quiet.Tick += (_, _) =>
        {
            if (Decisions.Due(DateTime.Now) is not { } screenshot) return;
            _quiet.Stop();
            Screenshot?.Invoke(screenshot);
        };
    }

    public ScreenshotWatcher Decisions { get; } = new();

    /// <summary>A screenshot is on the clipboard and has been left alone long enough to read.</summary>
    public event Action<ClipboardChange>? Screenshot;

    /// <summary>"Park screenshots on the shelf": off, the clipboard isn't listened to at all.</summary>
    public bool Enabled
    {
        get => Decisions.Enabled;
        set
        {
            Decisions.Enabled = value;
            if (value && !_listening) _listening = Native.AddClipboardFormatListener(_window.Handle);
            else if (!value && _listening)
            {
                Native.RemoveClipboardFormatListener(_window.Handle);
                _listening = false;
                _quiet.Stop();
            }
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Native.WM_CLIPBOARDUPDATE) return IntPtr.Zero;
        handled = true;
        try
        {
            // Each change starts the quiet time again; one that isn't a screenshot means there's none to wait for.
            _quiet.Stop();
            if (Decisions.Notice(Describe(), out var skipped)) _quiet.Start();
            else if (skipped is not null) Log.Write($"clipboard: {skipped}");
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Log.Write(ex);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// The program that copied what's on the clipboard; and only when it could be a screenshot, the formats, and whether
    /// the program asked for it to stay out of clipboard history.
    /// </summary>
    static ClipboardChange Describe()
    {
        var sequence = Native.GetClipboardSequenceNumber();
        var owner = Native.GetClipboardOwner();
        uint process = 0;
        if (owner != IntPtr.Zero) Native.GetWindowThreadProcessId(owner, out process);
        var program = owner == IntPtr.Zero ? null : Native.ProgramOf(process) ?? "";
        var change = new ClipboardChange(program, [], sequence, DateTime.Now) { FromTanjelo = process == Environment.ProcessId };
        if (change.FromTanjelo || !ScreenshotWatcher.MightBeScreenshot(program)) return change;

        var formats = new List<string>();
        var historyAllowed = true;
        if (!OpenBriefly()) return change;
        try
        {
            for (var format = Native.EnumClipboardFormats(0); format != 0; format = Native.EnumClipboardFormats(format))
            {
                var name = Native.ClipboardFormatName(format);
                formats.Add(name);
                if (name == "CanIncludeInClipboardHistory") historyAllowed = Dword(format) != 0;
            }
        }
        finally
        {
            Native.CloseClipboard();
        }
        return change with { Formats = formats, HistoryAllowed = historyAllowed };
    }

    /// <summary>Opens the clipboard for a moment, trying a few times while the app that copied still has it open.</summary>
    static bool OpenBriefly()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (Native.OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(10);
        }
        return false;
    }

    static int Dword(uint format)
    {
        var handle = Native.GetClipboardData(format);
        var pointer = handle == IntPtr.Zero ? IntPtr.Zero : Native.GlobalLock(handle);
        if (pointer == IntPtr.Zero) return 1;
        try
        {
            return Marshal.ReadInt32(pointer);
        }
        finally
        {
            Native.GlobalUnlock(handle);
        }
    }

    public void Dispose()
    {
        Enabled = false;
        _window.Dispose();
    }
}
