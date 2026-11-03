using System.Diagnostics;

namespace Tanjelo.Setup;

/// <summary>
/// The uninstaller can't delete itself while it runs, so as it ends it leaves that to a hidden command prompt, as
/// Velopack's Update.exe did: it waits for the uninstaller to exit, deletes that one file, and removes the install
/// folder only if that leaves it empty.
/// </summary>
static class SelfDelete
{
    /// <summary>
    /// The command prompt's arguments, or null for a path the command prompt would read wrongly: one with a % sign,
    /// which it could take for a variable. The uninstaller then stays, and the log says so.
    /// </summary>
    public static string? Arguments(string exe, string folder)
    {
        if ((exe + folder).IndexOfAny(['%', '"']) >= 0) return null;
        // Up to 30 tries a second apart, while the uninstaller (and, for everyone, the copy that started it) exits.
        return $"/d /v:off /c \"for /l %i in (1,1,30) do @(if exist \"{exe}\" (del /f /q \"{exe}\" >nul 2>&1 & if exist \"{exe}\" choice /c y /n /d y /t 1 >nul)) & rmdir \"{folder}\" >nul 2>&1\"";
    }

    public static void Schedule(string exe, string folder, Log log)
    {
        if (Arguments(exe, folder) is not { } arguments)
        {
            log.Write($"{exe} stays: delete it and its folder by hand.");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.SystemDirectory,
            })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            log.Write($"{exe} stays: {ex.Message}");
        }
    }
}
