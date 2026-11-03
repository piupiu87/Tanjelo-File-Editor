using System.Text;

namespace Tanjelo.Setup;

/// <summary>Where setup has got to, and for the copy, how many of its bytes are written.</summary>
sealed record Report(Step Step, long Done = 0, long Total = 0);

enum Step { Checking, Copying, Registering, MovingOver, Removing }

/// <summary>A synchronous <see cref="IProgress{T}"/>: reports arrive in order, on the thread that makes them.</summary>
sealed class Reporter<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>What a list of installed files says about the install it belongs to.</summary>
sealed record FileList(string Version, Scope Scope, IReadOnlyList<string> Files);

/// <summary>
/// Installs, updates and uninstalls Tanjelo's files and its entries in Windows, with no window of its own. It always
/// installs into a folder of Tanjelo's own and writes down every file it puts there, so uninstalling removes those
/// files and nothing else: anything someone added stays, with the folder holding it.
/// </summary>
sealed class Installer(Places places, Log log)
{
    public const string AppName = "Tanjelo.exe";
    public const string UninstallerName = "Uninstall Tanjelo.exe";
    public const string ListName = "installed-files.txt";
    public const string ShortcutName = "Tanjelo.lnk";

    // Setup's own working folders inside the install folder: the new files, then the old ones they replace.
    const string NewFiles = "~setup-new";
    const string OldFiles = "~setup-old";

    public Places Places => places;

    /// <summary>The Tanjelo on this PC: the one for everyone first, then this user's.</summary>
    public Installed? Find()
    {
        foreach (var scope in new[] { Scope.Machine, Scope.User })
        {
            if (AppsEntry.Read(places, scope) is not { } found) continue;
            // A 1.1 install is moved over even if its folder has gone, so its entries go too.
            if (found.FromVelopack || ReadList(found.Folder) is not null) return found;
            log.Write($"Apps lists Tanjelo in {found.Folder}, but it isn't there any more.");
        }
        return null;
    }

    public string DefaultFolder(Scope scope) => Path.Combine(places.DefaultParent(scope), "Tanjelo");

    /// <summary>Tanjelo always goes in a folder of its own: D:\Apps becomes D:\Apps\Tanjelo, D:\Apps\Tanjelo stays.</summary>
    public static string OwnFolder(string chosen)
    {
        var folder = Folders.Trim(chosen.Trim().Trim('"'));
        return string.Equals(Path.GetFileName(folder), "Tanjelo", StringComparison.OrdinalIgnoreCase) ? folder : Path.Combine(folder, "Tanjelo");
    }

    /// <summary>
    /// Where a run installs: an installed Tanjelo is updated where it is, whatever was asked, since there's never more
    /// than one; otherwise the folder asked for, or the scope's default. Says why when it isn't what was asked.
    /// </summary>
    public (string Folder, Scope Scope, string? Note) Choose(Installed? existing, bool allUsers, string? folder)
    {
        if (existing is { FromVelopack: false })
        {
            var asked = allUsers != (existing.Scope == Scope.Machine) || folder is not null && !Folders.Same(OwnFolder(folder), existing.Folder);
            return (existing.Folder, existing.Scope, asked ? $"Tanjelo is already installed in {existing.Folder}, so it's updated there." : null);
        }
        var scope = allUsers ? Scope.Machine : Scope.User;
        return (folder is null ? DefaultFolder(scope) : OwnFolder(folder), scope, null);
    }

    /// <summary>Why Tanjelo can't be installed in <paramref name="folder"/>, or null if it can.</summary>
    public string? Problem(string folder, Installed? existing)
    {
        string full;
        try
        {
            if (folder.StartsWith(@"\\", StringComparison.Ordinal) || folder.StartsWith("//", StringComparison.Ordinal)) return Strings.ChooseAFolderOnThisPC;
            if (!Path.IsPathRooted(folder) || folder.Length < 3 || folder[1] != ':') return Strings.ThatIsnTAFolderSetupCanUse;
            full = Folders.Trim(Path.GetFullPath(folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return Strings.ThatIsnTAFolderSetupCanUse;
        }
        var root = Path.GetPathRoot(full) ?? "";
        if (Folders.Same(full, root)) return Strings.ChooseAFolderNotAWholeDrive;
        // .NET Core no longer checks names itself: each folder must be one Windows can make.
        if (full.Substring(root.Length).Split('\\').Any(name => name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.TrimEnd(' ', '.').Length != name.Length))
            return Strings.ThatIsnTAFolderSetupCanUse;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveType is DriveType.Network) return Strings.ChooseAFolderOnThisPC;
            if (drive.DriveType is DriveType.CDRom or DriveType.NoRootDirectory) return Strings.ThatIsnTAFolderSetupCanUse;
        }
        catch (ArgumentException) { return Strings.ThatIsnTAFolderSetupCanUse; }
        if (Folders.Within(full, places.Windows)) return Strings.ChooseAFolderOutsideTheWindowsFolder;
        foreach (var data in new[] { places.DataFolder, places.SettingsFolder })
            if (Folders.Within(full, data))
                return string.Format(Strings.TanjeloKeepsItsOwnFilesInChoose, data);
        if (File.Exists(full)) return Strings.ThatIsnTAFolderSetupCanUse;
        if (existing is { FromVelopack: false } && Folders.Same(full, existing.Folder)) return null;
        // A folder with something in it is only used if it's a Tanjelo install: its files are known, so nothing else is lost.
        if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any() && ReadList(full) is null)
            return string.Format(Strings.AlreadyHasFilesInItChooseAn, full);
        return null;
    }

    /// <summary>
    /// Installs or updates Tanjelo in <paramref name="folder"/> and registers it. The new files are unpacked beside the
    /// old ones first, so cancelling or failing during the copy leaves the folder as it was; then they're swapped in,
    /// files the new version no longer has are removed, and the list is rewritten.
    /// </summary>
    public void Install(string folder, Scope scope, Payload payload, IProgress<Report>? progress, CancellationToken cancel)
    {
        folder = Folders.Trim(Path.GetFullPath(folder));
        progress?.Report(new Report(Step.Checking));
        payload.Verify(cancel);
        var existed = Directory.Exists(folder);
        var staged = Path.Combine(folder, NewFiles);
        try
        {
            Folders.DeleteOwn(staged);
            Folders.DeleteOwn(Path.Combine(folder, OldFiles));
            var total = payload.Size;
            progress?.Report(new Report(Step.Copying, 0, total));
            payload.Extract(staged, done => progress?.Report(new Report(Step.Copying, done, total)), cancel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            Folders.DeleteOwn(staged);
            if (!existed) Folders.RemoveEmpty(folder, folder);
            if (ex is OperationCanceledException) throw;
            log.Write($"copying into {folder}: {ex.Message}");
            throw new SetupException(ex is UnauthorizedAccessException
                ? string.Format(Strings.SetupCanTWriteToForAFolder, folder)
                : string.Format(Strings.CouldnTCopyTanjelo, ex.Message));
        }
        SwapIn(folder, staged, scope, payload);
        progress?.Report(new Report(Step.Registering));
        Register(folder, scope, payload);
    }

    void SwapIn(string folder, string staged, Scope scope, Payload payload)
    {
        var old = Path.Combine(folder, OldFiles);
        var before = ReadList(folder)?.Files ?? [];
        var after = payload.Files.Select(f => f.Path).ToList();
        var moves = new Stack<(string From, string To)>();
        void Move(string from, string to)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Move(from, to);
            moves.Push((from, to));
        }
        try
        {
            var list = Path.Combine(folder, ListName);
            if (File.Exists(list)) Move(list, Path.Combine(old, ListName));
            foreach (var file in after)
            {
                var target = Path.Combine(folder, file);
                if (File.Exists(target)) Move(target, Path.Combine(old, file));
                Move(Path.Combine(staged, file), target);
            }
            var kept = new HashSet<string>(after, StringComparer.OrdinalIgnoreCase);
            foreach (var file in before.Where(f => !kept.Contains(f)))
                if (Folders.Inside(folder, file) is { } path && File.Exists(path))
                    Move(path, Path.Combine(old, file));
            WriteList(folder, new FileList(payload.Version, scope, after));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Write($"replacing the files in {folder}: {ex.Message}");
            while (moves.Count > 0)
            {
                var (from, to) = moves.Pop();
                try { File.Move(to, from); }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { log.Write($"putting back {from}: {undo.Message}"); }
            }
            Folders.DeleteOwn(staged);
            Folders.DeleteOwn(old);
            // Folders made for the new files go; an update's folder still holds the old ones, so it stays.
            foreach (var file in after)
                Folders.RemoveEmpty(Path.GetDirectoryName(Path.Combine(folder, file))!, folder);
            throw new SetupException(string.Format(Strings.CouldnTReplaceTanjeloSFiles, ex.Message));
        }
        Folders.DeleteOwn(old);
        Folders.DeleteOwn(staged);
        // Folders that held only files the new version doesn't have.
        foreach (var file in before)
            if (Folders.Inside(folder, file) is { } path)
                Folders.RemoveEmpty(Path.GetDirectoryName(path)!, folder);
    }

    /// <summary>The Apps entry, the Start menu entry and the PATH entry, for the scope.</summary>
    public void Register(string folder, Scope scope, Payload payload)
    {
        try
        {
            AppsEntry.Write(places, scope, folder, payload.Version, payload.Size);
            Shortcut.Create(Path.Combine(places.StartMenu(scope), ShortcutName), Path.Combine(folder, AppName), Strings.ConvertAndEditFilesStraightFrom);
            PathVariable.Add(places, scope, folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException)
        {
            log.Write($"registering {folder}: {ex}");
            throw new SetupException(string.Format(Strings.CouldnTAddTanjeloToWindows, ex.Message));
        }
    }

    /// <summary>
    /// Removes the files setup put in <paramref name="folder"/>, then the folders that leaves empty, then the Start menu,
    /// PATH and Apps entries. <paramref name="running"/>, the uninstaller in use, goes once it has exited. Returns the
    /// files that couldn't be removed; then the list keeps them, with the uninstaller and the Apps entry, to try again.
    /// </summary>
    public IReadOnlyList<string> Uninstall(string folder, string? running, IProgress<Report>? progress)
    {
        folder = Folders.Trim(Path.GetFullPath(folder));
        var list = ReadList(folder) ?? throw new SetupException(string.Format(Strings.TanjeloIsnTInstalledIn, folder));
        progress?.Report(new Report(Step.Removing, 0, list.Files.Count));
        var left = new List<string>();
        var done = 0;
        foreach (var file in list.Files)
        {
            var path = Folders.Inside(folder, file);
            if (path is null) log.Write($"left {file} alone: it isn't a path inside {folder}");
            else if (running is not null && Folders.Same(path, running)) { }
            else if (!Folders.DeleteFile(path)) left.Add(file);
            progress?.Report(new Report(Step.Removing, ++done, list.Files.Count));
        }
        if (left.Count > 0)
        {
            log.Write($"couldn't remove: {string.Join(", ", left)}");
            WriteList(folder, list with { Files = left.Concat(list.Files.Where(f => f.Equals(UninstallerName, StringComparison.OrdinalIgnoreCase))).ToList() });
        }
        else
        {
            Folders.DeleteFile(Path.Combine(folder, ListName));
        }
        // Folders the removed files leave empty go, the install folder too once nothing is left in it.
        foreach (var file in list.Files.OrderByDescending(f => f.Length))
            if (Folders.Inside(folder, file) is { } path)
                Folders.RemoveEmpty(Path.GetDirectoryName(path)!, folder);
        if (left.Count > 0) return left;
        Unregister(folder, list.Scope);
        if (running is not null && Folders.Within(running, folder)) SelfDelete.Schedule(running, folder, log);
        return [];
    }

    void Unregister(string folder, Scope scope)
    {
        try
        {
            var shortcut = Path.Combine(places.StartMenu(scope), ShortcutName);
            if (Shortcut.Target(shortcut) is { } target && Folders.Within(target, folder)) Folders.DeleteFile(shortcut);
            PathVariable.Remove(places, scope, folder);
            if (AppsEntry.Read(places, scope) is { } entry && Folders.Same(entry.Folder, folder)) AppsEntry.Delete(places, scope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.Write($"unregistering {folder}: {ex}");
            throw new SetupException(string.Format(Strings.CouldnTRemoveTanjeloFromWindows, ex.Message));
        }
    }

    /// <summary>
    /// The user's own data in %LOCALAPPDATA%\Tanjelo, as uninstalling has always removed it: the log, the update check's
    /// answer, pictures held on the shelf and WebView2's cache. Anything else there stays. Settings, in %APPDATA%\Tanjelo,
    /// stay for a reinstall.
    /// </summary>
    public void RemoveData()
    {
        var data = places.DataFolder;
        foreach (var file in new[] { "log.txt", "update.json" }) Folders.DeleteFile(Path.Combine(data, file));
        foreach (var folder in new[] { "Clipboard", "WebView2" }) Folders.DeleteOwn(Path.Combine(data, folder));
        Folders.RemoveEmpty(data, data);
    }

    /// <summary>The list of installed files in <paramref name="folder"/>, or null when it isn't a Tanjelo install.</summary>
    public static FileList? ReadList(string folder)
    {
        var path = Path.Combine(folder, ListName);
        if (!File.Exists(path)) return null;
        string version = "", scope = "";
        var files = new List<string>();
        foreach (var line in File.ReadAllLines(path, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0))
        {
            if (line.StartsWith("# version:", StringComparison.Ordinal)) version = line.Substring(10).Trim();
            else if (line.StartsWith("# scope:", StringComparison.Ordinal)) scope = line.Substring(8).Trim();
            else if (!line.StartsWith("#", StringComparison.Ordinal)) files.Add(line);
        }
        return new FileList(version, scope == "machine" ? Scope.Machine : Scope.User, files);
    }

    static void WriteList(string folder, FileList list) =>
        File.WriteAllLines(Path.Combine(folder, ListName),
            new[]
            {
                "# The files setup put here, which Tanjelo's uninstaller removes. Anything else in this folder stays.",
                "# version: " + list.Version,
                "# scope: " + (list.Scope == Scope.Machine ? "machine" : "user"),
            }.Concat(list.Files),
            new UTF8Encoding(false));
}
