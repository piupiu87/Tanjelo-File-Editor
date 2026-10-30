using System.Diagnostics;
using System.Runtime.InteropServices;
using Tanjelo.ShellMenu;

namespace Tanjelo.Core.Tests;

/// <summary>
/// The Windows 11 menu entry. How it hands a selection to Tanjelo is checked here; the entry itself only once its package
/// is registered, which changes Windows, so that runs on CI (the windows11-menu job sets TANJELO_MENU_REGISTERED).
/// </summary>
public class Windows11MenuTests
{
    [Fact]
    public void A_selection_goes_on_the_command_line_or_in_a_file_when_long()
    {
        using var scratch = new Scratch();
        var start = MenuCommand.StartInfo(@"C:\Apps\Tanjelo\Tanjelo.exe", [@"C:\a b\photo.png", @"D:\folder"], scratch.Dir);
        Assert.Equal([@"C:\a b\photo.png", @"D:\folder"], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.Empty(scratch.Files());

        var many = Enumerable.Range(0, 600).Select(i => $@"C:\Users\someone\Pictures\Holiday 2026\photo-{i:000}-with-a-long-name.jpg").ToList();
        start = MenuCommand.StartInfo(@"C:\Apps\Tanjelo\Tanjelo.exe", many, scratch.Dir);
        Assert.Equal("--paths-file", start.ArgumentList[0]);
        Assert.Equal(many, File.ReadAllLines(start.ArgumentList[1]));
        // Tanjelo reads the same list back and deletes the file.
        Assert.Equal(many, App.Selection(start.ArgumentList));
        Assert.Empty(scratch.Files());
    }

    [ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IExplorerCommand
    {
        [PreserveSig] int GetTitle(IntPtr items, out IntPtr title);
        [PreserveSig] int GetIcon(IntPtr items, out IntPtr icon);
        [PreserveSig] int GetToolTip(IntPtr items, out IntPtr tip);
        [PreserveSig] int GetCanonicalName(out Guid name);
        [PreserveSig] int GetState(IntPtr items, int okToBeSlow, out uint state);
        [PreserveSig] int Invoke(IntPtr items, IntPtr bindContext);
        [PreserveSig] int GetFlags(out uint flags);
        [PreserveSig] int EnumSubCommands(out IntPtr commands);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr item);

    [DllImport("shell32.dll", PreserveSig = false)]
    static extern void SHCreateShellItemArrayFromShellItem(IntPtr item, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr array);

    [Fact]
    public void The_registered_entry_is_named_Tanjelo_and_hands_the_selection_over()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("TANJELO_MENU_REGISTERED") == "1",
            "Needs the menu's package registered, which only the CI job does.");
        using var scratch = new Scratch();
        var photo = scratch.Image("menu test.png");

        // Created out of process, in the package's surrogate, as Explorer creates it.
        var command = (IExplorerCommand)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid(MenuCommand.Clsid), throwOnError: true)!)!;
        Assert.Equal(0, command.GetTitle(IntPtr.Zero, out var title));
        Assert.Equal("Tanjelo…", Marshal.PtrToStringUni(title));
        Marshal.FreeCoTaskMem(title);
        Assert.Equal(0, command.GetState(IntPtr.Zero, 0, out var state));
        Assert.Equal(0u, state);
        Assert.Equal(0, command.GetIcon(IntPtr.Zero, out var icon));
        Assert.EndsWith(@"\Tanjelo.exe,0", Marshal.PtrToStringUni(icon));
        Marshal.FreeCoTaskMem(icon);

        SHCreateItemFromParsingName(photo, IntPtr.Zero, new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), out var item);
        SHCreateShellItemArrayFromShellItem(item, new Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), out var array);
        var before = DateTime.Now;
        Assert.Equal(0, command.Invoke(array, IntPtr.Zero));
        Marshal.Release(array);
        Marshal.Release(item);

        // Tanjelo started, to show its menu for the file (nothing else starts it on the runner); stopped here, as nobody
        // is there to pick.
        Process? started = null;
        for (var i = 0; i < 50 && started is null; i++)
        {
            started = Process.GetProcessesByName("Tanjelo").FirstOrDefault(p => p.StartTime >= before.AddSeconds(-1));
            if (started is null) Thread.Sleep(100);
        }
        Assert.NotNull(started);
        started.Kill();
    }
}
