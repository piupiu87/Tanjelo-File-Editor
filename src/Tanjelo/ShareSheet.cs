using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Tanjelo;

/// <summary>
/// Opens the Windows Share sheet for files, where Nearby Sharing also appears when it's turned on. A tray app has no
/// window to share from, so the sheet is anchored to a one-pixel invisible window at the pointer, the documented
/// route for desktop apps.
/// </summary>
/// <remarks>
/// Windows only opens the sheet for a window that may come to the front, which Tanjelo may right after the menu's
/// click or Enter. Explorer's own Share command was tried as a fallback, but it needs Explorer's window around it and
/// fails (or crashes) when run from another app, so a sheet that doesn't open is reported instead.
/// </remarks>
static class ShareSheet
{
    /// <summary>Opens the sheet, and completes once it's done with the files (or after a while, to be safe).</summary>
    public static async Task ShowAsync(IReadOnlyList<string> files)
    {
        var items = new List<IStorageItem>();
        foreach (var file in files)
            items.Add(await StorageFile.GetFileFromPathAsync(file));

        var anchor = Anchor();
        var done = new TaskCompletionSource();
        var requested = new TaskCompletionSource();
        anchor.Closed += (_, _) => done.TrySetResult();
        // The sheet is done when an app is picked, when it closes and the anchor gets the focus back, or after a while.
        var lostFocus = false;
        anchor.Deactivated += (_, _) => lostFocus = true;
        anchor.Activated += (_, _) =>
        {
            if (lostFocus) anchor.Close();
        };
        var giveUp = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        giveUp.Tick += (_, _) => anchor.Close();
        anchor.Closed += (_, _) => giveUp.Stop();
        anchor.Show();
        anchor.Activate();
        giveUp.Start();
        var hwnd = new WindowInteropHelper(anchor).Handle;

        string? problem = null;
        try
        {
            var manager = DataTransferManagerInterop.GetForWindow(hwnd);
            manager.DataRequested += (_, e) =>
            {
                var data = e.Request.Data;
                data.Properties.Title = files.Count == 1 ? Path.GetFileName(files[0]) : string.Format(Strings.Files2, files.Count);
                data.SetStorageItems(items, readOnly: true);
                requested.TrySetResult();
            };
            manager.TargetApplicationChosen += (_, _) => anchor.Dispatcher.BeginInvoke(anchor.Close);
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
            // A sheet that opens asks for the files at once; one that doesn't is ignored without an error.
            if (await Task.WhenAny(requested.Task, Task.Delay(TimeSpan.FromSeconds(3))) != requested.Task)
                problem = Strings.WindowsDidnTAskForTheFiles;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            problem = ex.Message;
        }

        if (problem is not null)
        {
            Log.Write($"share sheet: {problem}");
            anchor.Close();
            throw new Core.ConversionException(Strings.WindowsDidnTOpenTheShareSheet);
        }
        await done.Task;
    }

    static Window Anchor()
    {
        Native.GetCursorPos(out var pointer);
        var anchor = new Window
        {
            Title = "Tanjelo Share",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 1,
            Height = 1,
        };
        anchor.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(anchor).Handle;
            Native.HideFromAltTab(handle);
            Native.SetWindowPos(handle, IntPtr.Zero, pointer.X, pointer.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER);
        };
        return anchor;
    }
}
