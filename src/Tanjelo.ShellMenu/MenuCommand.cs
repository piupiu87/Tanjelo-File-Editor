using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Tanjelo.ShellMenu;

/// <summary>
/// The one entry, "Tanjelo…". Explorer asks for its title, icon and state while drawing the menu, so those answer at
/// once from what's known; Invoke only hands the selection to Tanjelo.exe, which opens its usual menu at the pointer,
/// as Ctrl+Shift+Space does, and returns.
/// </summary>
[GeneratedComClass, Guid(Clsid)]
internal sealed partial class MenuCommand : IExplorerCommand
{
    /// <summary>The class Explorer creates; the sparse package's manifest names it too.</summary>
    public const string Clsid = "6E0F7A4B-2C1D-4E8A-9F3B-5A7C2D1E0B94";

    /// <summary>Longer command lines than this go through a file: Windows allows about 32,000 characters.</summary>
    const int LongestCommandLine = 30000;

    public int GetTitle(IShellItemArray? items, out nint title)
    {
        title = Marshal.StringToCoTaskMemUni("Tanjelo…");
        return Native.S_OK;
    }

    public int GetIcon(IShellItemArray? items, out nint icon)
    {
        icon = Marshal.StringToCoTaskMemUni(App + ",0");
        return Native.S_OK;
    }

    public int GetToolTip(IShellItemArray? items, out nint tip)
    {
        tip = 0;
        return Native.E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid name)
    {
        name = new Guid(Clsid);
        return Native.S_OK;
    }

    public int GetState(IShellItemArray? items, int okToBeSlow, out uint state)
    {
        state = 0; // ECS_ENABLED, for files, folders and any mix of them
        return Native.S_OK;
    }

    public int Invoke(IShellItemArray? items, nint bindContext)
    {
        try
        {
            var paths = Paths(items);
            if (paths.Count == 0) return Native.S_OK;
            Start(App, paths, Path.GetTempPath());
            return Native.S_OK;
        }
        catch (Exception)
        {
            return Native.E_FAIL;
        }
    }

    public int GetFlags(out uint flags)
    {
        flags = 0; // ECF_DEFAULT: one entry, no submenu
        return Native.S_OK;
    }

    public int EnumSubCommands(out nint commands)
    {
        commands = 0;
        return Native.E_NOTIMPL;
    }

    /// <summary>The files and folders selected, as paths; items that aren't on disk, such as a phone's, are left out.</summary>
    static List<string> Paths(IShellItemArray? items)
    {
        var paths = new List<string>();
        if (items is null || items.GetCount(out var count) != Native.S_OK) return paths;
        for (uint i = 0; i < count; i++)
        {
            if (items.GetItemAt(i, out var item) != Native.S_OK) continue;
            if (item.GetDisplayName(Native.SIGDN_FILESYSPATH, out var name) != Native.S_OK) continue;
            paths.Add(Marshal.PtrToStringUni(name)!);
            Marshal.FreeCoTaskMem(name);
        }
        return paths;
    }

    /// <summary>
    /// Starts Tanjelo with the selection, as "Send to" does: on the command line, or in a file it reads and deletes
    /// (<c>--paths-file</c>) when that would be too long. Tanjelo may take the foreground, to show its menu.
    /// </summary>
    static void Start(string app, IReadOnlyList<string> paths, string temp)
    {
        var start = StartInfo(app, paths, temp);
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        Process.Start(start)?.Dispose();
    }

    internal static ProcessStartInfo StartInfo(string app, IReadOnlyList<string> paths, string temp)
    {
        var start = new ProcessStartInfo(app) { UseShellExecute = false };
        if (paths.Sum(p => p.Length + 3) + app.Length < LongestCommandLine)
        {
            foreach (var path in paths) start.ArgumentList.Add(path);
        }
        else
        {
            var list = Path.Combine(temp, $"tanjelo-selection-{Guid.NewGuid():N}.txt");
            File.WriteAllLines(list, paths);
            start.ArgumentList.Add("--paths-file");
            start.ArgumentList.Add(list);
        }
        return start;
    }

    /// <summary>Tanjelo.exe, beside this DLL in the install folder.</summary>
    static string App => Path.Combine(Path.GetDirectoryName(Module.Path)!, "Tanjelo.exe");
}

/// <summary>Where this DLL is: Explorer loads it into a surrogate process, whose own folder is System32.</summary>
static unsafe class Module
{
    public static string Path { get; } = Find();

    static string Find()
    {
        delegate* unmanaged<int> self = &Exports.DllCanUnloadNow;
        if (!Native.GetModuleHandleEx(Native.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | Native.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (nint)self, out var module))
            return AppContext.BaseDirectory;
        var name = stackalloc char[1024];
        var length = Native.GetModuleFileName(module, name, 1024);
        return new string(name, 0, (int)length);
    }
}

/// <summary>Makes <see cref="MenuCommand"/>s for COM.</summary>
[GeneratedComClass]
internal sealed partial class ClassFactory : IClassFactory
{
    public int CreateInstance(nint outer, in Guid iid, out nint instance)
    {
        instance = 0;
        if (outer != 0) return Native.CLASS_E_NOAGGREGATION;
        return Exports.Query(new MenuCommand(), iid, out instance);
    }

    public int LockServer(int lockIt) => Native.S_OK;
}

/// <summary>The two functions every in-process COM server exports.</summary>
static unsafe class Exports
{
    static readonly StrategyBasedComWrappers Wrappers = new();

    [UnmanagedCallersOnly(EntryPoint = nameof(DllGetClassObject))]
    public static int DllGetClassObject(Guid* clsid, Guid* iid, nint* factory)
    {
        *factory = 0;
        if (*clsid != new Guid(MenuCommand.Clsid)) return Native.CLASS_E_CLASSNOTAVAILABLE;
        var result = Query(new ClassFactory(), *iid, out var found);
        *factory = found;
        return result;
    }

    /// <summary>Stays loaded while Explorer runs: the menu's objects are few and small.</summary>
    [UnmanagedCallersOnly(EntryPoint = nameof(DllCanUnloadNow))]
    public static int DllCanUnloadNow() => Native.S_FALSE;

    /// <summary>The interface <paramref name="iid"/> of a managed object, for a native caller.</summary>
    public static int Query(object instance, Guid iid, out nint found)
    {
        var unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in iid, out found);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
