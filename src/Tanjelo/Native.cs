using System.Runtime.InteropServices;
using System.Text;

namespace Tanjelo;

static class Native
{
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0;
    public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);

    public static string ClassName(IntPtr hwnd)
    {
        var name = new StringBuilder(256);
        return GetClassName(hwnd, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    public static RECT WorkAreaAt(POINT point)
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), ref info);
        return info.rcWork;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    /// <summary>Keeps a window out of Alt+Tab and the taskbar, as a tool window.</summary>
    public static void HideFromAltTab(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((style | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Rounded Windows 11 corners, and a dark frame/shadow in dark mode.</summary>
    public static void StyleWindow(IntPtr hwnd, bool dark)
    {
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var darkMode = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr ILCreateFromPath(string path);

    [DllImport("shell32.dll")]
    static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern uint ILGetSize(IntPtr pidl);

    [DllImport("shell32.dll")]
    static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);

    /// <summary>
    /// The paths as a shell ID list array (CFSTR_SHELLIDLIST), as Explorer's own drags carry them: Explorer makes
    /// shortcuts only to items named this way. Null when a path can't be found.
    /// </summary>
    public static byte[]? ShellIdList(IReadOnlyList<string> paths)
    {
        // A CIDA: the count, the offsets of the parent folder's ID list and of each item's, then the lists. The parent
        // is the desktop, whose list is empty, so each item's is its full one.
        var lists = new List<byte[]> { new byte[2] };
        foreach (var path in paths)
        {
            var pidl = ILCreateFromPath(path);
            if (pidl == IntPtr.Zero) return null;
            try
            {
                var list = new byte[ILGetSize(pidl)];
                Marshal.Copy(pidl, list, 0, list.Length);
                lists.Add(list);
            }
            finally
            {
                ILFree(pidl);
            }
        }
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(paths.Count);
            var offset = sizeof(int) * (lists.Count + 1);
            foreach (var list in lists)
            {
                writer.Write(offset);
                offset += list.Length;
            }
            foreach (var list in lists) writer.Write(list);
        }
        return stream.ToArray();
    }

    /// <summary>Opens (or reuses) an Explorer window on the folder with all the given items selected.</summary>
    public static void RevealInExplorer(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        var folder = Path.GetDirectoryName(paths[0])!;
        var siblings = paths.Where(p => string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)).ToList();
        var folderPidl = ILCreateFromPath(folder);
        var itemPidls = siblings.Select(ILCreateFromPath).Where(p => p != IntPtr.Zero).ToList();
        try
        {
            if (folderPidl == IntPtr.Zero || itemPidls.Count == 0) throw new IOException("Path not found.");
            SHOpenFolderAndSelectItems(folderPidl, (uint)itemPidls.Count, itemPidls.Select(ILFindLastID).ToArray(), 0);
        }
        finally
        {
            if (folderPidl != IntPtr.Zero) ILFree(folderPidl);
            itemPidls.ForEach(ILFree);
        }
    }
}
