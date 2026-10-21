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
            link.Description = "Convert with Tanjelo";
            link.IconLocation = ExePath + ",0";
            link.Save();
        }
    }

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
