using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Tanjelo;

/// <summary>Start-with-Windows, the "Send to" menu entry, and shell thumbnails.</summary>
static class ShellIntegration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "Tanjelo";

    static string ExePath => Environment.ProcessPath!;

    public static bool StartsWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(AppName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(AppName, $"\"{ExePath}\" --autostart");
            else key.DeleteValue(AppName, throwOnMissingValue: false);
        }
    }

    static string SendToShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "Tanjelo.lnk");

    public static bool InSendToMenu
    {
        get => File.Exists(SendToShortcut);
        set
        {
            if (!value)
            {
                File.Delete(SendToShortcut);
                return;
            }
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(SendToShortcut);
            link.TargetPath = ExePath;
            link.Description = Strings.ConvertWithTanjelo;
            link.IconLocation = ExePath + ",0";
            link.Save();
        }
    }


    // ---- The entry in the Windows 11 right-click menu: a sparse package beside the app (signed releases bring it),
    // registered for this user while the setting is on. Its COM server, TanjeloMenu.dll, hands the selection to
    // Tanjelo.exe. The package points at this folder, which Velopack keeps the same across updates.

    const string MenuPackageName = "Tanjelo.Menu";

    static string MenuPackage => Path.Combine(AppContext.BaseDirectory, "Tanjelo.ShellMenu.msix");

    /// <summary>Whether this copy can add the entry: Windows 11, with the package and its DLL beside the app.</summary>
    public static bool CanUseWindows11Menu =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && File.Exists(MenuPackage)
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "TanjeloMenu.dll"));

    public static bool InWindows11Menu
    {
        get => CanUseWindows11Menu && RegisteredMenu() is not null;
        set
        {
            if (value == InWindows11Menu) return;
            var packages = new Windows.Management.Deployment.PackageManager();
            // Waited for off this thread: the deployment finishes on the thread pool, which mustn't need the UI thread.
            var result = value
                ? Task.Run(() => packages.AddPackageByUriAsync(new Uri(MenuPackage),
                    new Windows.Management.Deployment.AddPackageOptions { ExternalLocationUri = new Uri(AppContext.BaseDirectory) }).AsTask()).GetAwaiter().GetResult()
                : Task.Run(() => packages.RemovePackageAsync(RegisteredMenu()!.Id.FullName).AsTask()).GetAwaiter().GetResult();
            if (result.ExtendedErrorCode is { } error) throw new IOException(result.ErrorText, error);
        }
    }

    static Windows.ApplicationModel.Package? RegisteredMenu() =>
        new Windows.Management.Deployment.PackageManager().FindPackagesForUser("").FirstOrDefault(p => p.Id.Name == MenuPackageName);

    /// <summary>The Explorer thumbnail (or icon) for a file or folder, as Explorer would draw it.</summary>
    public static ImageSource? Thumbnail(string path, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0 /* SIIGBF_RESIZETOFIT */, out var hbitmap) != 0)
                return null;
            try
            {
                return ToBitmapSource(hbitmap);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch (Exception ex) when (ex is COMException or FileNotFoundException or ArgumentException)
        {
            return null;
        }
    }

    // Reads the DIB section directly: CreateBitmapSourceFromHBitmap drops the alpha channel.
    static BitmapSource? ToBitmapSource(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<DIBSECTION>(), out var dib) == 0 || dib.dsBm.bmBits == IntPtr.Zero || dib.dsBm.bmBitsPixel != 32)
            return null;
        int width = dib.dsBm.bmWidth, height = dib.dsBm.bmHeight, stride = dib.dsBm.bmWidthBytes;
        var pixels = new byte[stride * height];
        Marshal.Copy(dib.dsBm.bmBits, pixels, 0, pixels.Length);
        if (dib.dsBmih.biHeight > 0) // bottom-up rows
        {
            var flipped = new byte[pixels.Length];
            for (var y = 0; y < height; y++)
                Buffer.BlockCopy(pixels, y * stride, flipped, (height - 1 - y) * stride, stride);
            pixels = flipped;
        }
        var source = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory item);

    [DllImport("gdi32.dll")]
    static extern int GetObject(IntPtr handle, int size, out DIBSECTION dib);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr handle);
}

/// <summary>The settings Windows keeps rather than settings.json, so the settings window can be tested without changing them.</summary>
interface IWindowsSettings
{
    bool StartsWithWindows { get; set; }
    bool InSendToMenu { get; set; }

    /// <summary>Whether the Windows 11 menu entry can be offered at all: Windows 11, and a signed release, which brings it.</summary>
    bool CanUseWindows11Menu { get; }
    bool InWindows11Menu { get; set; }
}

sealed class WindowsSettings : IWindowsSettings
{
    public bool StartsWithWindows
    {
        get => ShellIntegration.StartsWithWindows;
        set => ShellIntegration.StartsWithWindows = value;
    }

    public bool InSendToMenu
    {
        get => ShellIntegration.InSendToMenu;
        set => ShellIntegration.InSendToMenu = value;
    }

    public bool CanUseWindows11Menu => ShellIntegration.CanUseWindows11Menu;

    public bool InWindows11Menu
    {
        get => ShellIntegration.InWindows11Menu;
        set => ShellIntegration.InWindows11Menu = value;
    }
}
