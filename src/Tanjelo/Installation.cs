using System.Security.Cryptography;
using System.Text;

namespace Tanjelo;

/// <summary>
/// What Tanjelo does for setup and its uninstaller. <c>--quit</c>: the Tanjelo in the tray quits, so its files can be
/// replaced or removed. <c>--before-uninstall</c>: take away what Tanjelo can add to Windows for the user (Start with
/// Windows, the "Send to" entry and the Windows 11 menu entry), so nothing is left behind; the uninstaller removes the
/// rest. Settings in %APPDATA%\Tanjelo stay for a reinstall. Neither shows anything.
/// </summary>
static class Installation
{
    /// <summary>The folder this Tanjelo runs from, where setup installed it.</summary>
    static string InstallFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    /// <summary>
    /// The event the Tanjelo in the tray waits on to quit, one per folder, so quitting one never closes a copy run from
    /// somewhere else. Setup works out the same name from the install folder (Tanjelo.Setup.TanjeloProcesses).
    /// </summary>
    internal static string QuitEventName(string folder) =>
        @"Local\Tanjelo.Quit." + Convert.ToHexStringLower(SHA256.HashData(Encoding.Unicode.GetBytes(Path.TrimEndingDirectorySeparator(folder).ToUpperInvariant())), 0, 8);

    /// <summary>The quit event for this copy, which only the copy in the tray makes.</summary>
    internal static EventWaitHandle QuitEvent() => new(false, EventResetMode.AutoReset, QuitEventName(InstallFolder));

    /// <summary>Asks the Tanjelo in the tray from this folder to quit, and waits up to 15 seconds. 0 once it's gone.</summary>
    public static int Quit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName(InstallFolder), out var quit)) return 0;
        using (quit) quit.Set();
        // The copy in the tray holds this mutex while it runs.
        using var running = new Mutex(false, App.TrayMutex);
        try
        {
            if (!running.WaitOne(TimeSpan.FromSeconds(15))) return 1;
        }
        catch (AbandonedMutexException)
        {
            // It ended without letting go, which is how it always ends; the mutex is ours now.
        }
        running.ReleaseMutex();
        return 0;
    }

    public static void BeforeUninstall()
    {
        Try(() => ShellIntegration.StartsWithWindows = false);
        Try(() => ShellIntegration.InSendToMenu = false);
        Try(() => ShellIntegration.InWindows11Menu = false);
    }

    static void Try(Action step)
    {
        try { step(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                   or System.Runtime.InteropServices.COMException)
        {
            Log.Write($"before uninstalling: {ex.Message}");
        }
    }
}
