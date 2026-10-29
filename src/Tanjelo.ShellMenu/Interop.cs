using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Tanjelo.ShellMenu;

// The shell's interfaces this DLL uses, declared for the COM source generator, which NativeAOT needs. Only the methods
// used have proper signatures; the others keep their places in the vtable with plain pointers.

/// <summary>A command in Explorer's menu: <c>IExplorerCommand</c>.</summary>
[GeneratedComInterface, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9")]
internal partial interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IShellItemArray? items, out nint title);
    [PreserveSig] int GetIcon(IShellItemArray? items, out nint icon);
    [PreserveSig] int GetToolTip(IShellItemArray? items, out nint tip);
    [PreserveSig] int GetCanonicalName(out Guid name);
    [PreserveSig] int GetState(IShellItemArray? items, int okToBeSlow, out uint state);
    [PreserveSig] int Invoke(IShellItemArray? items, nint bindContext);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int EnumSubCommands(out nint commands);
}

/// <summary>The selection Explorer passes: <c>IShellItemArray</c>.</summary>
[GeneratedComInterface, Guid("b63ea76d-1f85-456f-a19c-48159efa858b")]
internal partial interface IShellItemArray
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
    [PreserveSig] int GetPropertyStore(int flags, in Guid iid, out nint result);
    [PreserveSig] int GetPropertyDescriptionList(nint key, in Guid iid, out nint result);
    [PreserveSig] int GetAttributes(int flags, uint mask, out uint attributes);
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemAt(uint index, out IShellItem item);
    [PreserveSig] int EnumItems(out nint items);
}

/// <summary>One selected file or folder: <c>IShellItem</c>.</summary>
[GeneratedComInterface, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
internal partial interface IShellItem
{
    [PreserveSig] int BindToHandler(nint bindContext, in Guid handler, in Guid iid, out nint result);
    [PreserveSig] int GetParent(out nint parent);
    [PreserveSig] int GetDisplayName(uint form, out nint name);
    [PreserveSig] int GetAttributes(uint mask, out uint attributes);
    [PreserveSig] int Compare(nint other, uint hint, out int order);
}

/// <summary>What COM asks for an instance through: <c>IClassFactory</c>.</summary>
[GeneratedComInterface, Guid("00000001-0000-0000-C000-000000000046")]
internal partial interface IClassFactory
{
    [PreserveSig] int CreateInstance(nint outer, in Guid iid, out nint instance);
    [PreserveSig] int LockServer(int lockIt);
}

static partial class Native
{
    public const int S_OK = 0, S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001), E_NOINTERFACE = unchecked((int)0x80004002), E_FAIL = unchecked((int)0x80004005);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110), CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const uint SIGDN_FILESYSPATH = 0x80058000;
    public const uint GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x4, GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT = 0x2;
    public const int ASFW_ANY = -1;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetModuleHandleEx(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW")]
    public static unsafe partial uint GetModuleFileName(nint module, char* name, uint size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);
}
