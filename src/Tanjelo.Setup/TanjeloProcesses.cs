using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Tanjelo.Setup;

/// <summary>The installed Tanjelo as a running program: setup asks it to quit, and starts it when it's done.</summary>
interface ITanjeloProcesses
{
    /// <summary>
    /// The ids of Tanjelo's programs running from <paramref name="folder"/> (Tanjelo, tanj, a 1.1 updater); never setup
    /// itself, nor the uninstaller and its elevated copy, which run from that folder too.
    /// </summary>
    IReadOnlyList<int> In(string folder);

    /// <summary>Asks the Tanjelo in the tray, if it's the one from <paramref name="folder"/>, to quit, as <c>Tanjelo.exe --quit</c> does.</summary>
    void AskToQuit(string folder);

    /// <summary>Stops one program, if it's still running from <paramref name="folder"/>.</summary>
    void Stop(int id, string folder);

    /// <summary>Runs <c>Tanjelo.exe --before-uninstall</c>, which takes away what Tanjelo added to Windows for this user.</summary>
    void BeforeUninstall(string exe);

    /// <summary>Starts Tanjelo as the user, never as administrator: drag and drop and the shortcut need it at Explorer's level.</summary>
    void Start(string exe);
}

sealed class TanjeloProcesses(Log log) : ITanjeloProcesses
{
    /// <summary>
    /// The event the Tanjelo in the tray waits on, one per install folder, so asking one Tanjelo to quit never closes
    /// another (a copy unzipped somewhere else, say). Tanjelo.exe works out the same name from its own folder.
    /// </summary>
    public static string QuitEventName(string folder)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.Unicode.GetBytes(Folders.Trim(folder).ToUpperInvariant()));
        return @"Local\Tanjelo.Quit." + string.Concat(hash.Take(8).Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// The programs that are Tanjelo, by name: the app and tanj, and in a 1.1 folder Velopack's updater (its launcher is
    /// Tanjelo.exe too). The uninstaller runs from the install folder as well, and its elevated copy from the same file,
    /// so anything else there is never taken for Tanjelo.
    /// </summary>
    static readonly string[] Programs = ["Tanjelo.exe", "tanj.exe", "Update.exe"];

    internal static bool IsTanjelo(string path) => Programs.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    // Setup itself, whatever its name and wherever it runs from: never listed, never stopped.
    static readonly int Self = Process.GetCurrentProcess().Id;
    static readonly string? SelfImage = ImagePath(Self);

    static bool Ours(int id, string? path, string folder) =>
        id != Self && path is not null && Folders.Within(path, folder) && IsTanjelo(path) && !(SelfImage is not null && Folders.Same(path, SelfImage));

    public IReadOnlyList<int> In(string folder)
    {
        var found = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
                if (Ours(process.Id, ImagePath(process.Id), folder)) found.Add(process.Id);
        }
        return found;
    }

    public void AskToQuit(string folder)
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName(folder), out var quit)) return;
        using (quit) quit.Set();
    }

    public void Stop(int id, string folder)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            // The id may have been taken by another program since it was listed; and setup never stops itself.
            if (ImagePath(id) is not { } path || !Ours(id, path, folder)) return;
            log.Write($"stopping {path} ({id})");
            process.Kill();
            process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            log.Write($"stopping {id}: {ex.Message}");
        }
    }

    public void BeforeUninstall(string exe)
    {
        if (!File.Exists(exe)) return;
        try
        {
            using var run = Process.Start(new ProcessStartInfo(exe, "--before-uninstall") { UseShellExecute = false, WorkingDirectory = Environment.SystemDirectory });
            if (run is not null && !run.WaitForExit(30_000)) log.Write($"{exe} --before-uninstall took more than 30 seconds");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Write($"{exe} --before-uninstall: {ex.Message}");
        }
    }

    public void Start(string exe)
    {
        try
        {
            // Explorer starts it at its own level when setup itself was started as administrator.
            var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
            var start = elevated
                ? new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"\"{exe}\"")
                : new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe) };
            start.UseShellExecute = true;
            Process.Start(start)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Write($"starting {exe}: {ex.Message}");
        }
    }

    static string? ImagePath(int id)
    {
        var handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var path = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
