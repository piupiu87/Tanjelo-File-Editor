using System.Runtime.InteropServices;

namespace Tanjelo;

/// <summary>Reads the selected files from the foreground File Explorer window (or the desktop) via the Shell's COM API.</summary>
static class ExplorerSelection
{
    static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    const int CSIDL_DESKTOP = 0, SWC_DESKTOP = 8, SWFO_NEEDDISPATCH = 1;

    public static bool IsDesktop(string className) => className is "Progman" or "WorkerW";
    public static bool IsExplorer(string className) => className is "CabinetWClass" or "ExploreWClass";
    public static bool IsFileWindow(IntPtr hwnd) => Native.ClassName(hwnd) is var c && (IsDesktop(c) || IsExplorer(c));

    public static IReadOnlyList<string> Get(IntPtr hwnd) => GetWithFolder(hwnd).Files;

    /// <summary>
    /// The selected files and the folder they're in: the folder open in the Explorer window, or the desktop. The folder
    /// is null when it isn't one a file can be saved in, such as This PC, a zip or a CD.
    /// </summary>
    public static (IReadOnlyList<string> Files, string? Folder) GetWithFolder(IntPtr hwnd)
    {
        var className = Native.ClassName(hwnd);
        if (IsDesktop(className)) return FromDesktop();
        if (IsExplorer(className)) return FromWindow(hwnd);
        return ([], null);
    }

    static IShellWindows ShellWindows() =>
        (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid)!)!;

    static (IReadOnlyList<string>, string?) FromWindow(IntPtr hwnd)
    {
        // Windows 11 tabs share one top-level window; the first ShellTabWindowClass child is the visible tab.
        var activeTab = Native.FindWindowEx(hwnd, IntPtr.Zero, "ShellTabWindowClass", null);
        var windows = ShellWindows();
        for (var i = 0; i < windows.Count; i++)
        {
            object? window = windows.Item(i);
            if (window is null) continue;
            try
            {
                dynamic browser = window;
                if (new IntPtr(Convert.ToInt64(browser.HWND)) != hwnd) continue;
                if (activeTab != IntPtr.Zero && TabOf(window) is var tab && tab != IntPtr.Zero && tab != activeTab) continue;
                return (Paths(browser.Document.SelectedItems()), Writable(FolderOf(browser.Document)));
            }
            catch (COMException) { continue; } // windows can close or navigate while we enumerate
            finally
            {
                Marshal.ReleaseComObject(window);
            }
        }
        return ([], null);
    }

    static (IReadOnlyList<string>, string?) FromDesktop()
    {
        object location = CSIDL_DESKTOP, root = null!;
        dynamic desktop = ShellWindows().FindWindowSW(ref location, ref root, SWC_DESKTOP, out _, SWFO_NEEDDISPATCH);
        return (Paths(desktop.Document.SelectedItems()), Writable(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)));
    }

    static string? FolderOf(dynamic view)
    {
        try
        {
            return view.Folder.Self.Path;
        }
        catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }

    /// <summary>The folder, if a file can be saved there: not a virtual folder such as This PC ("::{…}") or a CD.</summary>
    static string? Writable(string? folder)
    {
        if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return null;
        try
        {
            return new DriveInfo(folder).DriveType == DriveType.CDRom ? null : folder;
        }
        catch (ArgumentException)
        {
            return folder; // a network share, which DriveInfo doesn't take
        }
    }

    static IntPtr TabOf(object browser)
    {
        if (browser is not IComServiceProvider provider) return IntPtr.Zero;
        var service = SID_STopLevelBrowser;
        var iid = typeof(IShellBrowser).GUID;
        if (provider.QueryService(ref service, ref iid, out var found) != 0 || found is not IShellBrowser shellBrowser)
            return IntPtr.Zero;
        return shellBrowser.GetWindow(out var tab) == 0 ? tab : IntPtr.Zero;
    }

    static IReadOnlyList<string> Paths(dynamic items)
    {
        var paths = new List<string>();
        int count = items.Count;
        for (var i = 0; i < count; i++)
        {
            string path = items.Item(i).Path;
            // Skips virtual items (Control Panel, items inside a zip folder...) that have no real path.
            if (File.Exists(path) || Directory.Exists(path))
                paths.Add(path);
        }
        return paths;
    }

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item([MarshalAs(UnmanagedType.Struct)] object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass);
        int RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int swClass);
        void Revoke(int cookie);
        void OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int swClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object found);
    }

    // Only the first method (inherited from IOleWindow) is needed.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        [PreserveSig]
        int GetWindow(out IntPtr hwnd);
    }
}
