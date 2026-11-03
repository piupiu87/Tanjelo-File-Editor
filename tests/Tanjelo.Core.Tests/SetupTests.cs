using Microsoft.Win32;
using Tanjelo.Setup;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Setup and the uninstaller, on a sandbox PC (<see cref="SetupSandbox"/>): installing for one user and for everyone,
/// updating, moving a 1.1 install over and uninstalling, with decoy files beside and inside the install folder that
/// must survive. The real thing runs on CI, which installs and uninstalls on a clean runner.
/// </summary>
public class SetupTests
{
    static readonly CancellationToken None = CancellationToken.None;

    static readonly Func<bool> Never = () => false, Always = () => true;

    static string Text(string path) => File.ReadAllText(path);

    [Fact]
    public async Task Installs_just_for_me_with_every_file_listed_and_registered()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        var (folder, scope, note) = pc.Installer.Choose(pc.Installer.Find(), allUsers: false, folder: null);
        Assert.Equal(Path.Combine(pc.Places.LocalAppData, "Programs", "Tanjelo"), folder);
        Assert.Equal(Scope.User, scope);
        Assert.Null(note);
        Assert.Null(pc.Installer.Problem(folder, null));

        var reports = new List<Report>();
        await pc.Job.InstallAsync(folder, scope, payload, null, new Reporter<Report>(reports.Add), Never, None);

        Assert.Equal("Tanjelo 1.2.0", Text(Path.Combine(folder, "Tanjelo.exe")));
        Assert.Equal("loader 1.2.0", Text(Path.Combine(folder, @"runtimes\win-x64\native\WebView2Loader.dll")));
        var list = Installer.ReadList(folder)!;
        Assert.Equal("1.2.0", list.Version);
        Assert.Equal(Scope.User, list.Scope);
        Assert.Equal(SetupSandbox.App("1.2.0").Select(f => f.Path).Order(), list.Files.Order());
        Assert.Equal(new[] { Installer.ListName, Installer.UninstallerName, "licenses", "runtimes", "tanj.exe", "Tanjelo.exe" }.Order(StringComparer.OrdinalIgnoreCase),
            Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));

        using (var entry = pc.AppsEntry(Scope.User)!)
        {
            Assert.Equal("Tanjelo", entry.GetValue("DisplayName"));
            Assert.Equal("1.2.0", entry.GetValue("DisplayVersion"));
            Assert.Equal("piupiu87", entry.GetValue("Publisher"));
            Assert.Equal(folder, entry.GetValue("InstallLocation"));
            Assert.Equal($"\"{folder}\\Uninstall Tanjelo.exe\"", entry.GetValue("UninstallString"));
            Assert.Equal($"\"{folder}\\Uninstall Tanjelo.exe\" --silent", entry.GetValue("QuietUninstallString"));
        }
        Assert.Null(pc.AppsEntry(Scope.Machine));
        Assert.Equal(Path.Combine(folder, "Tanjelo.exe"), Shortcut.Target(Path.Combine(pc.Places.UserStartMenu, "Tanjelo.lnk")), ignoreCase: true);
        Assert.False(File.Exists(Path.Combine(pc.Places.CommonStartMenu, "Tanjelo.lnk")));
        Assert.Equal(folder, pc.UserPath);
        Assert.Equal("", pc.MachinePath);
        Assert.Equal(new Installed(Scope.User, folder, "1.2.0", false), pc.Installer.Find());

        Assert.Equal([Step.Checking, Step.Copying, Step.Registering], reports.Select(r => r.Step).Distinct());
        var total = payload.Size;
        Assert.All(reports.Where(r => r.Step == Step.Copying), r => Assert.Equal(total, r.Total));
        Assert.Equal(total, reports.Last(r => r.Step == Step.Copying).Done);
        Assert.Equal(0, pc.Elevation.Runs);
        Assert.Empty(pc.Tanjelo.Started);
    }

    [Fact]
    public async Task Installs_for_everyone_through_one_elevated_copy()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));
        var (folder, scope, _) = pc.Installer.Choose(null, allUsers: true, folder: null);
        Assert.Equal(Path.Combine(pc.Places.ProgramFiles, "Tanjelo"), folder);

        var reports = new List<Report>();
        await pc.Job.InstallAsync(folder, scope, payload, null, new Reporter<Report>(reports.Add), Never, None);

        Assert.Equal(1, pc.Elevation.Runs);
        Assert.Equal(0, pc.Elevation.ExitCode);
        Assert.Equal(Scope.Machine, Installer.ReadList(folder)!.Scope);
        using (var entry = pc.AppsEntry(Scope.Machine)!)
            Assert.Equal(folder, entry.GetValue("InstallLocation"));
        Assert.Null(pc.AppsEntry(Scope.User));
        Assert.Equal(Path.Combine(folder, "Tanjelo.exe"), Shortcut.Target(Path.Combine(pc.Places.CommonStartMenu, "Tanjelo.lnk")), ignoreCase: true);
        Assert.False(File.Exists(Path.Combine(pc.Places.UserStartMenu, "Tanjelo.lnk")));
        Assert.Equal(folder, pc.MachinePath);
        Assert.Equal("", pc.UserPath);
        // The elevated copy's reports reach the wizard as they're made.
        Assert.Equal(payload.Size, reports.Last(r => r.Step == Step.Copying).Done);
        Assert.Equal(new Installed(Scope.Machine, folder, "1.2.0", false), pc.Installer.Find());

        // Already running as administrator, as winget runs it for everyone: no second copy.
        pc.Elevation.IsElevated = true;
        await pc.Job.InstallAsync(folder, scope, pc.Payload("1.2.1", SetupSandbox.App("1.2.1")), pc.Installer.Find(), null, Never, None);
        Assert.Equal(1, pc.Elevation.Runs);
        Assert.Equal("Tanjelo 1.2.1", Text(Path.Combine(folder, "Tanjelo.exe")));
    }

    [Fact]
    public async Task Without_administrator_permission_nothing_is_installed_for_everyone()
    {
        using var pc = new SetupSandbox();
        pc.Elevation.Refused = true;
        var folder = pc.Installer.DefaultFolder(Scope.Machine);
        var refused = await Assert.ThrowsAsync<SetupException>(() => pc.Job.InstallAsync(folder, Scope.Machine, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None));
        Assert.Equal(ExitCodes.NoAdministrator, refused.ExitCode);
        Assert.False(Directory.Exists(folder));
        Assert.Null(pc.AppsEntry(Scope.Machine));
    }

    [Fact]
    public async Task Uninstalling_removes_only_what_setup_put_there()
    {
        using var pc = new SetupSandbox();
        var apps = Directory.CreateDirectory(pc.PathOf("Apps")).FullName;
        var (folder, scope, _) = pc.Installer.Choose(null, allUsers: false, folder: apps);
        Assert.Equal(Path.Combine(apps, "Tanjelo"), folder);
        await pc.Job.InstallAsync(folder, scope, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);

        // Decoys: a file beside the install folder, a file added inside it and one inside a folder setup made.
        File.WriteAllText(Path.Combine(apps, "decoy.txt"), "beside");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "inside");
        File.WriteAllText(Path.Combine(folder, "licenses", "mine.txt"), "deeper");
        // Tanjelo's data, with something else of the user's in that folder too, and the settings, which stay.
        var data = pc.Places.DataFolder;
        Directory.CreateDirectory(Path.Combine(data, "Clipboard"));
        File.WriteAllText(Path.Combine(data, "Clipboard", "held.png"), "picture");
        Directory.CreateDirectory(Path.Combine(data, "WebView2", "EBWebView"));
        File.WriteAllText(Path.Combine(data, "WebView2", "EBWebView", "cache"), "cache");
        File.WriteAllText(Path.Combine(data, "log.txt"), "log");
        File.WriteAllText(Path.Combine(data, "update.json"), "{}");
        File.WriteAllText(Path.Combine(data, "mine.txt"), "the user's");
        Directory.CreateDirectory(pc.Places.SettingsFolder);
        File.WriteAllText(Path.Combine(pc.Places.SettingsFolder, "settings.json"), "{}");
        // Someone else's entries on the PATH and in the Start menu.
        using (var environment = pc.Places.CurrentUser.CreateSubKey("Environment"))
            environment.SetValue("Path", @"%USERPROFILE%\bin;" + folder + @";C:\Tools", RegistryValueKind.ExpandString);
        File.WriteAllText(Path.Combine(pc.Places.UserStartMenu, "Other.lnk"), "not ours");

        var left = await pc.Job.UninstallAsync(folder, running: null, null, Never, None);

        Assert.Empty(left);
        Assert.Equal("beside", Text(Path.Combine(apps, "decoy.txt")));
        Assert.Equal("inside", Text(Path.Combine(folder, "notes.txt")));
        Assert.Equal("deeper", Text(Path.Combine(folder, "licenses", "mine.txt")));
        Assert.Equal(["licenses", "notes.txt"], Directory.GetFileSystemEntries(folder).Select(Path.GetFileName).Order());
        Assert.Equal(["mine.txt"], Directory.GetFileSystemEntries(Path.Combine(folder, "licenses")).Select(Path.GetFileName));
        Assert.Null(pc.AppsEntry(Scope.User));
        Assert.False(File.Exists(Path.Combine(pc.Places.UserStartMenu, "Tanjelo.lnk")));
        Assert.True(File.Exists(Path.Combine(pc.Places.UserStartMenu, "Other.lnk")));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools", pc.UserPath);
        Assert.Equal(["mine.txt"], Directory.GetFileSystemEntries(data).Select(Path.GetFileName));
        Assert.True(File.Exists(Path.Combine(pc.Places.SettingsFolder, "settings.json")));
        Assert.Equal([Path.Combine(folder, "Tanjelo.exe")], pc.Tanjelo.BeforeUninstallRuns);
        Assert.Null(pc.Installer.Find());
    }

    [Fact]
    public async Task Uninstalling_an_untouched_install_takes_its_folder_too_and_nothing_beside_it()
    {
        using var pc = new SetupSandbox();
        var apps = Directory.CreateDirectory(pc.PathOf("Apps")).FullName;
        File.WriteAllText(Path.Combine(apps, "decoy.txt"), "beside");
        var folder = Installer.OwnFolder(apps);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);

        Assert.Empty(await pc.Job.UninstallAsync(folder, running: null, null, Never, None));

        Assert.False(Directory.Exists(folder));
        Assert.Equal(["decoy.txt"], Directory.GetFileSystemEntries(apps).Select(Path.GetFileName));
        Assert.Equal("", pc.UserPath);
    }

    [Fact]
    public async Task Uninstalling_for_everyone_goes_through_the_elevated_copy_and_the_data_through_the_user()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.Machine);
        await pc.Job.InstallAsync(folder, Scope.Machine, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);
        Directory.CreateDirectory(pc.Places.DataFolder);
        File.WriteAllText(Path.Combine(pc.Places.DataFolder, "log.txt"), "log");

        Assert.Empty(await pc.Job.UninstallAsync(folder, running: null, null, Never, None));

        Assert.Equal(2, pc.Elevation.Runs);
        Assert.False(Directory.Exists(folder));
        Assert.Null(pc.AppsEntry(Scope.Machine));
        Assert.False(File.Exists(Path.Combine(pc.Places.CommonStartMenu, "Tanjelo.lnk")));
        Assert.Equal("", pc.MachinePath);
        Assert.False(Directory.Exists(pc.Places.DataFolder));
        Assert.Single(pc.Tanjelo.BeforeUninstallRuns);
    }

    [Fact]
    public async Task An_update_replaces_the_files_and_removes_the_ones_the_new_version_lacks()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", [.. SetupSandbox.App("1.2.0"), ("old.dll", "old"), (@"gone\only.txt", "only")]), null, null, Never, None);
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "mine");
        pc.Tanjelo.Run(Path.Combine(folder, "Tanjelo.exe"));

        var existing = pc.Installer.Find();
        Assert.Equal(new Installed(Scope.User, folder, "1.2.0", false), existing);
        Assert.Null(pc.Installer.Problem(folder, existing));
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.3.0", [.. SetupSandbox.App("1.3.0"), ("new.dll", "new")]), existing, null, Never, None);

        Assert.Equal([folder], pc.Tanjelo.AskedToQuit);
        Assert.Equal("Tanjelo 1.3.0", Text(Path.Combine(folder, "Tanjelo.exe")));
        Assert.Equal("new", Text(Path.Combine(folder, "new.dll")));
        Assert.False(File.Exists(Path.Combine(folder, "old.dll")));
        Assert.False(Directory.Exists(Path.Combine(folder, "gone")));
        Assert.Equal("mine", Text(Path.Combine(folder, "notes.txt")));
        Assert.False(Directory.Exists(Path.Combine(folder, "~setup-new")));
        Assert.False(Directory.Exists(Path.Combine(folder, "~setup-old")));
        Assert.Contains("new.dll", Installer.ReadList(folder)!.Files);
        Assert.DoesNotContain("old.dll", Installer.ReadList(folder)!.Files);
        Assert.Equal("1.3.0", pc.Installer.Find()!.Version);
        Assert.Equal(folder, pc.UserPath);
    }

    [Fact]
    public async Task Cancelling_the_copy_leaves_a_new_folder_out_and_an_update_as_it_was()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        using (var cancel = new CancellationTokenSource())
        {
            var reports = new Reporter<Report>(r => { if (r is { Step: Step.Copying, Done: > 0 }) cancel.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, reports, Never, cancel.Token));
        }
        Assert.False(Directory.Exists(folder));
        Assert.Null(pc.AppsEntry(Scope.User));
        Assert.Equal("", pc.UserPath);

        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);
        var before = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order().ToList();
        using (var cancel = new CancellationTokenSource())
        {
            var reports = new Reporter<Report>(r => { if (r is { Step: Step.Copying, Done: > 0 }) cancel.Cancel(); });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.3.0", [.. SetupSandbox.App("1.3.0"), ("new.dll", "new")]), pc.Installer.Find(), reports, Never, cancel.Token));
        }
        Assert.Equal(before, Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Order());
        Assert.Equal("Tanjelo 1.2.0", Text(Path.Combine(folder, "Tanjelo.exe")));
        Assert.Equal("1.2.0", pc.Installer.Find()!.Version);
    }

    [Fact]
    public async Task Cancelling_the_elevated_copy_undoes_its_copy_too()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.Machine);
        using var cancel = new CancellationTokenSource();
        var reports = new Reporter<Report>(r => { if (r is { Step: Step.Copying, Done: > 0 }) cancel.Cancel(); });
        // Slowed down, so the copy is still going when the word gets there.
        pc.Elevation.ReportPause = TimeSpan.FromMilliseconds(50);
        var big = Enumerable.Range(0, 40).Select(i => ($@"data\{i}.bin", new string('x', 1 << 16))).ToArray();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pc.Job.InstallAsync(folder, Scope.Machine, pc.Payload("1.2.0", [.. SetupSandbox.App("1.2.0"), .. big]), null, reports, Never, cancel.Token));
        Assert.Equal(ExitCodes.Cancelled, pc.Elevation.ExitCode);
        Assert.False(Directory.Exists(folder));
        Assert.Null(pc.AppsEntry(Scope.Machine));
    }

    [Fact]
    public async Task A_damaged_payload_is_refused_before_anything_is_copied()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        var damaged = await Assert.ThrowsAsync<SetupException>(() =>
            pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0"), damaged: true), null, null, Never, None));
        Assert.Equal(global::Tanjelo.Setup.Strings.SetupSOwnCopyOfTanjeloIsDamaged, damaged.Message);
        Assert.False(Directory.Exists(folder));

        // A zip naming a file outside the folder it's unpacked into is damaged too.
        var escaping = pc.Payload("1.2.0", [("Tanjelo.exe", "app"), (@"..\outside.txt", "escaped")]);
        await Assert.ThrowsAsync<SetupException>(() => pc.Job.InstallAsync(folder, Scope.User, escaping, null, null, Never, None));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "outside.txt")));
    }

    [Fact]
    public void Tanjelo_always_goes_in_a_folder_of_its_own()
    {
        Assert.Equal(@"D:\Apps\Tanjelo", Installer.OwnFolder(@"D:\Apps"));
        Assert.Equal(@"D:\Apps\Tanjelo", Installer.OwnFolder(@"D:\Apps\"));
        Assert.Equal(@"D:\Apps\Tanjelo", Installer.OwnFolder(@"D:\Apps\Tanjelo"));
        Assert.Equal(@"D:\Apps\tanjelo", Installer.OwnFolder(@"D:\Apps\tanjelo\"));
        Assert.Equal(@"D:\Tanjelo", Installer.OwnFolder(@"D:\"));
        Assert.Equal(@"D:\Apps\Tanjelo", Installer.OwnFolder("\"D:\\Apps\""));
    }

    [Fact]
    public async Task Setup_refuses_folders_it_mustnt_use()
    {
        using var pc = new SetupSandbox();
        var installer = pc.Installer;
        Assert.Equal(global::Tanjelo.Setup.Strings.ChooseAFolderNotAWholeDrive, installer.Problem(@"C:\", null));
        Assert.Equal(global::Tanjelo.Setup.Strings.ChooseAFolderOnThisPC, installer.Problem(@"\\server\share\Tanjelo", null));
        Assert.Equal(global::Tanjelo.Setup.Strings.ThatIsnTAFolderSetupCanUse, installer.Problem(@"Apps\Tanjelo", null));
        Assert.Equal(global::Tanjelo.Setup.Strings.ChooseAFolderOutsideTheWindowsFolder, installer.Problem(Path.Combine(pc.Places.Windows, "Tanjelo"), null));
        Assert.Equal(global::Tanjelo.Setup.Strings.ThatIsnTAFolderSetupCanUse, installer.Problem(pc.PathOf("Apps") + "\\Tan<jelo", null));
        // Tanjelo's own data and settings folders.
        Assert.StartsWith(global::Tanjelo.Setup.Strings.TanjeloKeepsItsOwnFilesInChoose[..20], installer.Problem(Installer.OwnFolder(pc.Places.LocalAppData), null));
        Assert.NotNull(installer.Problem(Path.Combine(pc.Places.SettingsFolder, "Tanjelo"), null));
        // A folder with someone's files in it, and a file.
        var busy = Directory.CreateDirectory(pc.PathOf(@"Busy\Tanjelo")).FullName;
        File.WriteAllText(Path.Combine(busy, "report.docx"), "someone's");
        Assert.Equal(string.Format(global::Tanjelo.Setup.Strings.AlreadyHasFilesInItChooseAn, busy), installer.Problem(busy, null));
        File.WriteAllText(pc.PathOf("Tanjelo"), "a file");
        Assert.Equal(global::Tanjelo.Setup.Strings.ThatIsnTAFolderSetupCanUse, installer.Problem(pc.PathOf("Tanjelo"), null));

        // Fine: a new folder, an empty one, and a Tanjelo install.
        Assert.Null(installer.Problem(pc.PathOf(@"New\Tanjelo"), null));
        Assert.Null(installer.Problem(Directory.CreateDirectory(pc.PathOf(@"Empty\Tanjelo")).FullName, null));
        var folder = installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);
        Assert.Null(installer.Problem(folder, null));
        Assert.Null(installer.Problem(folder, installer.Find()));
    }

    [Fact]
    public void An_installed_Tanjelo_is_updated_where_it_is_whatever_was_asked()
    {
        using var pc = new SetupSandbox();
        var mine = new Installed(Scope.User, @"D:\Apps\Tanjelo", "1.2.0", false);
        Assert.Equal((@"D:\Apps\Tanjelo", Scope.User, (string?)null), pc.Installer.Choose(mine, allUsers: false, folder: null));
        Assert.Equal((@"D:\Apps\Tanjelo", Scope.User, (string?)null), pc.Installer.Choose(mine, allUsers: false, folder: @"D:\Apps"));
        var (folder, scope, note) = pc.Installer.Choose(mine, allUsers: true, folder: @"E:\Other");
        Assert.Equal((@"D:\Apps\Tanjelo", Scope.User), (folder, scope));
        Assert.Contains(@"D:\Apps\Tanjelo", note);
        // A 1.1 install isn't kept where it is: it moves to where this one goes.
        var old = new Installed(Scope.User, Path.Combine(pc.Places.LocalAppData, "Tanjelo"), "1.1.0", true);
        Assert.Equal((pc.Installer.DefaultFolder(Scope.Machine), Scope.Machine, (string?)null), pc.Installer.Choose(old, allUsers: true, folder: null));
    }

    [Fact]
    public async Task Tanjelo_is_asked_to_quit_and_stopped_only_when_that_is_allowed()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);
        Assert.Empty(pc.Tanjelo.AskedToQuit); // nothing ran from there

        // A copy unzipped somewhere else is left alone.
        var elsewhere = pc.PathOf(@"Portable\Tanjelo.exe");
        pc.Tanjelo.Run(elsewhere);
        pc.Tanjelo.Run(Path.Combine(folder, "Tanjelo.exe"));
        pc.Tanjelo.QuitsWhenAsked = false;
        var stillRunning = await Assert.ThrowsAsync<SetupException>(() =>
            pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.1", SetupSandbox.App("1.2.1")), pc.Installer.Find(), null, Never, None));
        Assert.Equal(global::Tanjelo.Setup.Strings.TanjeloIsStillRunningCloseItFrom, stillRunning.Message);
        Assert.Equal("Tanjelo 1.2.0", Text(Path.Combine(folder, "Tanjelo.exe")));
        Assert.Empty(pc.Tanjelo.Stopped);

        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.1", SetupSandbox.App("1.2.1")), pc.Installer.Find(), null, Always, None);
        Assert.Equal([Path.Combine(folder, "Tanjelo.exe")], pc.Tanjelo.Stopped);
        Assert.Single(pc.Tanjelo.In(Path.GetDirectoryName(elsewhere)!));
        Assert.Equal("Tanjelo 1.2.1", Text(Path.Combine(folder, "Tanjelo.exe")));
    }

    /// <summary>A Tanjelo 1.1 install as Velopack left it, with Tanjelo's data in the same folder and Windows' entries pointing in.</summary>
    static string Velopack11(SetupSandbox pc)
    {
        var root = Path.Combine(pc.Places.LocalAppData, "Tanjelo");
        var current = Path.Combine(root, "current");
        foreach (var (path, text) in new[]
        {
            ("Update.exe", "velopack"), ("Tanjelo.exe", "stub"),
            (@"current\Tanjelo.exe", "Tanjelo 1.1.0"), (@"current\tanj.exe", "tanj"), (@"current\sq.version", "<package/>"),
            (@"current\licenses\Velopack-LICENSE.txt", "MIT"),
            (@"packages\Tanjelo-1.1.0-full.nupkg", "zip"), (@"packages\.betaId", "id"),
            // Tanjelo's own data, which stays.
            ("log.txt", "the log"), ("update.json", "{}"), (@"Clipboard\held.png", "picture"), (@"WebView2\EBWebView\Local State", "{}"),
            // And something nobody knows about, which stays too.
            ("decoy.txt", "the user's"),
        })
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        using (var entry = pc.Places.CurrentUser.CreateSubKey(global::Tanjelo.Setup.AppsEntry.KeyPath))
        {
            entry.SetValue("DisplayName", "Tanjelo");
            entry.SetValue("DisplayVersion", "1.1.0");
            entry.SetValue("InstallLocation", root);
            entry.SetValue("UninstallString", $"\"{root}\\Update.exe\" --uninstall");
            entry.SetValue("QuietUninstallString", $"\"{root}\\Update.exe\" --uninstall --silent");
        }
        using (var environment = pc.Places.CurrentUser.CreateSubKey("Environment"))
            environment.SetValue("Path", @"C:\Tools;" + current, RegistryValueKind.ExpandString);
        using (var run = pc.Places.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            run.SetValue("Tanjelo", $"\"{current}\\Tanjelo.exe\" --autostart");
        Shortcut.Create(Path.Combine(pc.Places.UserStartMenu, "Tanjelo.lnk"), Path.Combine(current, "Tanjelo.exe"), "Tanjelo");
        Shortcut.Create(Path.Combine(pc.Places.SendTo, "Tanjelo.lnk"), Path.Combine(current, "Tanjelo.exe"), "Convert with Tanjelo");
        Shortcut.Create(Path.Combine(pc.Places.TaskbarPins, "Tanjelo.lnk"), Path.Combine(current, "Tanjelo.exe"), "Tanjelo");
        Shortcut.Create(Path.Combine(pc.Places.Desktop, "Held picture.lnk"), Path.Combine(root, "Clipboard", "held.png"), "");
        var logs = Directory.CreateDirectory(Path.Combine(pc.Places.LocalAppData, "velopack")).FullName;
        File.WriteAllText(Path.Combine(logs, "velopack_Tanjelo.log"), "log");
        File.WriteAllText(Path.Combine(logs, "velopack_Other.log"), "another app's");
        Directory.CreateDirectory(Path.Combine(pc.Places.Temp, "velopack_Tanjelo"));
        return root;
    }

    [Fact]
    public async Task A_1_1_install_moves_over_with_its_data_kept_and_Velopack_gone()
    {
        using var pc = new SetupSandbox();
        var root = Velopack11(pc);
        pc.Tanjelo.Run(Path.Combine(root, "current", "Tanjelo.exe"));

        var existing = pc.Installer.Find();
        Assert.Equal(new Installed(Scope.User, root, "1.1.0", true), existing);
        var (folder, scope, _) = pc.Installer.Choose(existing, allUsers: false, folder: null);
        Assert.Null(pc.Installer.Problem(folder, existing));
        await pc.Job.InstallAsync(folder, scope, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), existing, null, Never, None);

        var exe = Path.Combine(folder, "Tanjelo.exe");
        Assert.Equal([root], pc.Tanjelo.AskedToQuit);
        // Velopack's files are gone; Tanjelo's data, and the file nobody knows, stay.
        Assert.Equal(["Clipboard", "decoy.txt", "log.txt", "update.json", "WebView2"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("picture", Text(Path.Combine(root, "Clipboard", "held.png")));
        Assert.Equal(["velopack_Other.log"], Directory.GetFileSystemEntries(Path.Combine(pc.Places.LocalAppData, "velopack")).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(pc.Places.Temp, "velopack_Tanjelo")));
        // Windows' entries point at the new copy.
        using (var entry = pc.AppsEntry(Scope.User)!)
            Assert.Equal($"\"{folder}\\Uninstall Tanjelo.exe\"", entry.GetValue("UninstallString"));
        Assert.Equal(@"C:\Tools;" + folder, pc.UserPath);
        Assert.Equal($"\"{exe}\" --autostart", SetupSandbox.Read(pc.Places.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Tanjelo"));
        foreach (var shortcut in new[] { Path.Combine(pc.Places.UserStartMenu, "Tanjelo.lnk"), Path.Combine(pc.Places.SendTo, "Tanjelo.lnk"), Path.Combine(pc.Places.TaskbarPins, "Tanjelo.lnk") })
            Assert.Equal(exe, Shortcut.Target(shortcut), ignoreCase: true);
        Assert.Equal(Path.Combine(root, "Clipboard", "held.png"), Shortcut.Target(Path.Combine(pc.Places.Desktop, "Held picture.lnk")), ignoreCase: true);
        Assert.Equal(new Installed(Scope.User, folder, "1.2.0", false), pc.Installer.Find());

        // Uninstalling the new copy then takes Tanjelo's data as Velopack's uninstall did, and leaves the unknown file.
        Assert.Empty(await pc.Job.UninstallAsync(folder, running: null, null, Never, None));
        Assert.Equal(["decoy.txt"], Directory.GetFileSystemEntries(root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_1_1_install_moves_over_to_everyone_leaving_no_entry_of_its_own()
    {
        using var pc = new SetupSandbox();
        var root = Velopack11(pc);
        var existing = pc.Installer.Find();
        var (folder, scope, _) = pc.Installer.Choose(existing, allUsers: true, folder: null);
        Assert.Equal(Scope.Machine, scope);
        await pc.Job.InstallAsync(folder, scope, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), existing, null, Never, None);

        Assert.Null(pc.AppsEntry(Scope.User));
        Assert.NotNull(pc.AppsEntry(Scope.Machine));
        Assert.False(File.Exists(Path.Combine(pc.Places.UserStartMenu, "Tanjelo.lnk")));
        Assert.Equal(@"C:\Tools", pc.UserPath);
        Assert.Equal(folder, pc.MachinePath);
        Assert.False(Directory.Exists(Path.Combine(root, "current")));
        Assert.True(File.Exists(Path.Combine(root, "Clipboard", "held.png")));
        Assert.Equal(new Installed(Scope.Machine, folder, "1.2.0", false), pc.Installer.Find());
    }

    [Fact]
    public async Task A_list_pointing_outside_the_folder_is_never_followed()
    {
        using var pc = new SetupSandbox();
        var apps = Directory.CreateDirectory(pc.PathOf("Apps")).FullName;
        var folder = Installer.OwnFolder(apps);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);
        var decoy = Path.Combine(apps, "decoy.txt");
        File.WriteAllText(decoy, "beside");
        var elsewhere = pc.PathOf("elsewhere.txt");
        File.WriteAllText(elsewhere, "elsewhere");
        File.AppendAllLines(Path.Combine(folder, Installer.ListName), [@"..\decoy.txt", elsewhere, @"licenses\..\..\decoy.txt", "C:decoy.txt"]);

        Assert.Empty(await pc.Job.UninstallAsync(folder, running: null, null, Never, None));
        Assert.True(File.Exists(decoy));
        Assert.True(File.Exists(elsewhere));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task Files_that_cant_be_removed_keep_the_list_the_uninstaller_and_the_Apps_entry()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);
        var held = Path.Combine(folder, "tanj.exe");
        IReadOnlyList<string> left;
        using (File.Open(held, FileMode.Open, FileAccess.Read, FileShare.None))
            left = await pc.Job.UninstallAsync(folder, running: Path.Combine(folder, Installer.UninstallerName), null, Never, None);

        Assert.Equal(["tanj.exe"], left);
        Assert.Equal(["tanj.exe", Installer.UninstallerName], Installer.ReadList(folder)!.Files);
        Assert.True(File.Exists(Path.Combine(folder, Installer.UninstallerName)));
        Assert.False(File.Exists(Path.Combine(folder, "Tanjelo.exe")));
        Assert.NotNull(pc.AppsEntry(Scope.User));

        // The next try gets the rest.
        Assert.Empty(await pc.Job.UninstallAsync(folder, running: null, null, Never, None));
        Assert.False(Directory.Exists(folder));
        Assert.Null(pc.AppsEntry(Scope.User));
    }

    [Fact]
    public async Task The_elevated_copy_passes_its_failures_back()
    {
        using var pc = new SetupSandbox();
        var folder = Directory.CreateDirectory(pc.PathOf(@"NotInstalled\Tanjelo")).FullName;
        var failure = await Assert.ThrowsAsync<SetupException>(() => pc.Elevation.RunAsync(new HelperRequest(HelperRequest.Uninstall, folder), null, None));
        Assert.Equal(string.Format(global::Tanjelo.Setup.Strings.TanjeloIsnTInstalledIn, folder), failure.Message);
        Assert.Equal(ExitCodes.Failed, pc.Elevation.ExitCode);
    }

    [Fact]
    public async Task The_uninstaller_in_use_is_deleted_once_it_has_exited()
    {
        using var pc = new SetupSandbox();
        Assert.Null(SelfDelete.Arguments(@"C:\100%\Tanjelo\Uninstall Tanjelo.exe", @"C:\100%\Tanjelo"));

        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);
        var uninstaller = Path.Combine(folder, Installer.UninstallerName);
        // Here it isn't running, so the hidden command prompt deletes it at once, then the folder.
        Assert.Empty(await pc.Job.UninstallAsync(folder, running: uninstaller, null, Never, None));
        for (var waited = 0; Directory.Exists(folder) && waited < 15_000; waited += 100) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void The_PATH_gets_the_folder_once_and_loses_only_it()
    {
        const string Folder = @"C:\Users\someone\AppData\Local\Programs\Tanjelo";
        Assert.Equal(Folder, PathVariable.WithFolder("", Folder));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools;" + Folder, PathVariable.WithFolder(@"%USERPROFILE%\bin;C:\Tools", Folder));
        Assert.Equal(@"C:\Tools;" + Folder, PathVariable.WithFolder(@"C:\Tools;", Folder));
        foreach (var path in new[] { Folder, Folder + @"\", Folder.ToUpperInvariant(), $"\"{Folder}\"", @"C:\Tools;" + Folder + ";" })
            Assert.Equal(path, PathVariable.WithFolder(path, Folder));

        Assert.Equal(@"%USERPROFILE%\bin;C:\Tools", PathVariable.WithoutFolder($@"%USERPROFILE%\bin;{Folder};C:\Tools", Folder));
        Assert.Equal(@"C:\Tools", PathVariable.WithoutFolder($@"C:\Tools;{Folder}\;{Folder.ToUpperInvariant()}", Folder));
        Assert.Equal("", PathVariable.WithoutFolder(Folder, Folder));
        Assert.Equal(@"C:\Tools;C:\Tanjelo-old", PathVariable.WithoutFolder(@"C:\Tools;C:\Tanjelo-old", @"C:\Tanjelo"));
    }

    [Fact]
    public void Tanjelo_and_setup_name_the_quit_event_alike()
    {
        foreach (var folder in new[] { @"C:\Program Files\Tanjelo", @"C:\Users\someone\AppData\Local\Programs\Tanjelo", @"D:\Apps\tanjelo" })
        {
            Assert.Equal(Installation.QuitEventName(folder), TanjeloProcesses.QuitEventName(folder));
            Assert.Equal(Installation.QuitEventName(folder + @"\"), TanjeloProcesses.QuitEventName(folder.ToUpperInvariant()));
        }
        Assert.NotEqual(TanjeloProcesses.QuitEventName(@"C:\Program Files\Tanjelo"), TanjeloProcesses.QuitEventName(@"D:\Portable\Tanjelo"));
    }

    [Fact]
    public void The_command_line()
    {
        var silent = Options.Parse(["--silent", "--all-users", "--dir", @"D:\My Apps"]);
        Assert.True(silent.Silent);
        Assert.True(silent.AllUsers);
        Assert.Equal(@"D:\My Apps", silent.Folder);
        Assert.Equal(@"D:\Apps", Options.Parse([@"--dir=D:\Apps"]).Folder);
        Assert.True(Options.Parse(["/S"]).Silent);
        Assert.False(Options.Parse([]).Silent);
        Assert.Equal(ExitCodes.BadCommandLine, Assert.Throws<SetupException>(() => Options.Parse(["--dir"])).ExitCode);
        Assert.Equal(ExitCodes.BadCommandLine, Assert.Throws<SetupException>(() => Options.Parse(["--quiet"])).ExitCode);

        // What setup hands its elevated copy comes back out the same.
        Assert.Equal("--dir", CommandLine.Quote("--dir"));
        Assert.Equal("\"C:\\Program Files\\Tanjelo\"", CommandLine.Quote(@"C:\Program Files\Tanjelo"));
        Assert.Equal("\"a \\\"quoted\\\" word\"", CommandLine.Quote("a \"quoted\" word"));
        Assert.Equal("\"ends in \\\\\"", CommandLine.Quote(@"ends in \"));
        Assert.Equal("\"\"", CommandLine.Quote(""));
    }
}
