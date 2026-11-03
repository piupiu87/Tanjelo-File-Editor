using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Tanjelo.Setup;

/// <summary>
/// Windows' own folder picker (the Open dialog choosing folders), for Browse…. WPF on .NET Framework has none, so it's
/// reached through the shell's IFileDialog.
/// </summary>
static class FolderPicker
{
    /// <summary>The folder chosen, or null if the dialog was cancelled.</summary>
    public static string? Pick(Window owner, string title, string start)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out var options);
            dialog.SetOptions(options | 0x20 /* FOS_PICKFOLDERS */ | 0x40 /* FOS_FORCEFILESYSTEM */ | 0x800 /* FOS_PATHMUSTEXIST */);
            dialog.SetTitle(title);
            // It opens on the nearest folder that exists above the one typed.
            for (var folder = start.Trim().Trim('"'); folder.Length > 0; folder = Path.GetDirectoryName(folder) ?? "")
            {
                if (!Directory.Exists(folder)) continue;
                if (SHCreateItemFromParsingName(folder, IntPtr.Zero, typeof(IShellItem).GUID, out var item) == 0)
                {
                    dialog.SetFolder(item);
                    Marshal.ReleaseComObject(item);
                }
                break;
            }
            if (dialog.Show(new WindowInteropHelper(owner).Handle) != 0) return null;
            dialog.GetResult(out var result);
            try
            {
                result.GetDisplayName(0x80058000 /* SIGDN_FILESYSPATH */, out var path);
                return path;
            }
            finally
            {
                Marshal.ReleaseComObject(result);
            }
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    class FileOpenDialog;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, IntPtr types);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);
}
