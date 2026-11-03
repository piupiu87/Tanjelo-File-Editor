using Microsoft.Win32;

namespace Tanjelo.Setup;

/// <summary>
/// Moves a Tanjelo 1.0 or 1.1 over to the new install. Velopack put those in %LOCALAPPDATA%\Tanjelo, the folder Tanjelo
/// also keeps its data in, and its own uninstaller would delete the whole folder, pictures held on the shelf included.
/// So it's never run: Velopack's files and entries are removed by name, and Tanjelo's data stays where it is. Start with
/// Windows, Send to and taskbar pins move to the new Tanjelo.exe.
/// </summary>
static class VelopackInstall
{
    /// <summary>What Velopack put in the folder: the app, its packages, its updater and the launcher beside them.</summary>
    static readonly string[] OwnFolders = ["current", "packages"];
    static readonly string[] OwnFiles = ["Update.exe", "Tanjelo.exe"];

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void MoveOver(Places places, Installed old, string newExe, Log log)
    {
        var root = old.Folder;
        void Step(string what, Action step)
        {
            try { step(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
            {
                // The new Tanjelo works whatever is left of the old one, so this never fails the install.
                log.Write($"moving 1.1 over, {what}: {ex.Message}");
            }
        }

        Step("Start with Windows", () =>
        {
            using var run = places.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (run?.GetValue("Tanjelo") is string command && Folders.Within(ExeOf(command), root))
                run.SetValue("Tanjelo", $"\"{newExe}\" --autostart");
        });
        Step("Send to", () => Retarget(Path.Combine(places.SendTo, Installer.ShortcutName), root, newExe));
        foreach (var folder in new[] { places.Desktop, places.TaskbarPins })
            if (Directory.Exists(folder))
                foreach (var shortcut in Directory.GetFiles(folder, "*.lnk"))
                    Step(shortcut, () => Retarget(shortcut, root, newExe));
        Step("the Start menu", () =>
        {
            // Setup has made its own entry by now; Velopack's, if it's still there, goes.
            var shortcut = Path.Combine(places.UserStartMenu, Installer.ShortcutName);
            if (Shortcut.Target(shortcut) is { } target && Folders.Within(target, root)) Folders.DeleteFile(shortcut);
        });
        Step("the PATH", () => PathVariable.Remove(places, Scope.User, Path.Combine(root, "current")));
        Step("Apps", () =>
        {
            if (AppsEntry.Read(places, Scope.User) is { FromVelopack: true }) AppsEntry.Delete(places, Scope.User);
        });
        Step("Velopack's files", () =>
        {
            foreach (var folder in OwnFolders) Folders.DeleteOwn(Path.Combine(root, folder));
            foreach (var file in OwnFiles) Folders.DeleteFile(Path.Combine(root, file));
            // Its log and its scratch folder, which it keeps elsewhere.
            var logs = Path.Combine(places.LocalAppData, "velopack");
            Folders.DeleteFile(Path.Combine(logs, "velopack_Tanjelo.log"));
            Folders.RemoveEmpty(logs, logs);
            Folders.DeleteOwn(Path.Combine(places.Temp, "velopack_Tanjelo"));
            // Tanjelo's data stays in the folder; if there's none, the folder goes.
            Folders.RemoveEmpty(root, root);
        });
        log.Write($"Moved Tanjelo {old.Version} over from {root}.");
    }

    /// <summary>Points a shortcut to the old Tanjelo at the new one; one to Velopack's updater goes. Any other stays.</summary>
    static void Retarget(string shortcut, string root, string newExe)
    {
        if (Shortcut.Target(shortcut) is not { } target) return;
        if (Folders.Same(target, Path.Combine(root, "current", Installer.AppName)) || Folders.Same(target, Path.Combine(root, Installer.AppName)))
            Shortcut.Retarget(shortcut, newExe);
        else if (Folders.Same(target, Path.Combine(root, "Update.exe")))
            Folders.DeleteFile(shortcut);
    }

    /// <summary>The program a command line starts: "C:\x\Tanjelo.exe" --autostart gives C:\x\Tanjelo.exe.</summary>
    static string ExeOf(string command)
    {
        command = command.Trim();
        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? command.Substring(1, end - 1) : command.Trim('"');
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? command.Substring(0, exe + 4) : command;
    }
}
