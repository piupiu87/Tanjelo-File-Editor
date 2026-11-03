using System.Security.AccessControl;
using System.Security.Principal;
using Tanjelo.Setup;
using SetupStrings = Tanjelo.Setup.Strings;

namespace Tanjelo.Core.Tests;

/// <summary>
/// What keeps setup from being turned against the PC it's on: a folder installed for everyone that only administrators
/// can change, and deletes that never go through a junction or symbolic link to someone else's files. On the sandbox PC
/// (<see cref="SetupSandbox"/>), where the user running the tests stands in for Administrators as the folder's owner.
/// </summary>
public class SetupSafetyTests
{
    static readonly CancellationToken None = CancellationToken.None;
    static readonly Func<bool> Never = () => false;

    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    const FileSystemRights Writes = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    /// <summary>Who may change something, by its rules.</summary>
    static HashSet<IdentityReference> Writers(IEnumerable<FileSystemAccessRule> rules) =>
        rules.Where(r => r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & Writes) != 0).Select(r => r.IdentityReference).ToHashSet();

    static string Text(string path) => File.ReadAllText(path);

    static void AssertWriters(HashSet<IdentityReference> expected, IEnumerable<FileSystemAccessRule> rules)
    {
        var actual = Writers(rules);
        Assert.True(expected.SetEquals(actual), "may change it: " + string.Join(", ", actual));
    }

    [Fact]
    public async Task For_everyone_the_folder_is_one_only_administrators_can_change_wherever_it_is()
    {
        using var pc = new SetupSandbox();
        // A folder of the user's own, as C:\Apps or D:\Apps would be: everything made in it would let its users change it.
        var apps = Directory.CreateDirectory(pc.PathOf("Apps")).FullName;
        var folder = Installer.OwnFolder(apps);
        Assert.Null(pc.Installer.Problem(folder, null, Scope.Machine));
        await pc.Job.InstallAsync(folder, Scope.Machine, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);

        HashSet<IdentityReference> expected = [FolderSecurity.LocalSystem, FolderSecurity.Administrators, SetupSandbox.Me];
        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.Equal(SetupSandbox.Me, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.True(security.AreAccessRulesProtected);
        var rules = Rules(security);
        Assert.All(rules, rule => Assert.False(rule.IsInherited));
        AssertWriters(expected, rules);
        Assert.Contains(rules, rule => rule.IdentityReference == Users && rule.FileSystemRights.HasFlag(FileSystemRights.ReadAndExecute));
        // What's in it takes those rules, and nothing from the folder above.
        foreach (var file in new[] { "Tanjelo.exe", @"runtimes\win-x64\native\WebView2Loader.dll" })
        {
            var inside = Rules(new FileInfo(Path.Combine(folder, file)).GetAccessControl());
            Assert.All(inside, rule => Assert.True(rule.IsInherited));
            AssertWriters(expected, inside);
        }

        // An update puts the rules back, should someone have loosened them.
        var loosened = new DirectoryInfo(folder).GetAccessControl();
        loosened.AddAccessRule(new FileSystemAccessRule(AuthenticatedUsers, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).SetAccessControl(loosened);
        Assert.Contains(AuthenticatedUsers, Writers(Rules(new DirectoryInfo(folder).GetAccessControl())));
        await pc.Job.InstallAsync(folder, Scope.Machine, pc.Payload("1.2.1", SetupSandbox.App("1.2.1")), pc.Installer.Find(), null, Never, None);
        AssertWriters(expected, Rules(new DirectoryInfo(folder).GetAccessControl()));
        AssertWriters(expected, Rules(new FileInfo(Path.Combine(folder, "Tanjelo.exe")).GetAccessControl()));
    }

    [Fact]
    public async Task For_everyone_a_folder_already_there_is_used_only_if_its_an_administrators_Tanjelo_install()
    {
        using var pc = new SetupSandbox();
        var payload = pc.Payload("1.2.0", SetupSandbox.App("1.2.0"));

        // Empty, but the user's: anything in it later could be the user's too.
        var empty = Directory.CreateDirectory(pc.PathOf(@"Shared\Tanjelo")).FullName;
        Assert.Equal(string.Format(SetupStrings.ForEveryoneOnThisPCChooseANewFolder, empty), pc.Installer.Problem(empty, null, Scope.Machine));
        Assert.Null(pc.Installer.Problem(empty, null, Scope.User));
        // The elevated copy checks for itself, whatever it's asked to do.
        var refused = await Assert.ThrowsAsync<SetupException>(() => pc.Job.InstallAsync(empty, Scope.Machine, payload, null, null, Never, None));
        Assert.Equal(string.Format(SetupStrings.ForEveryoneOnThisPCChooseANewFolder, empty), refused.Message);
        Assert.Empty(Directory.GetFileSystemEntries(empty));
        Assert.Null(pc.AppsEntry(Scope.Machine));

        // A Tanjelo installed just for a user isn't one for everyone either.
        var mine = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(mine, Scope.User, payload, null, null, Never, None);
        Assert.Equal(string.Format(SetupStrings.ForEveryoneOnThisPCChooseANewFolder, mine), pc.Installer.Problem(mine, null, Scope.Machine));

        // One for everyone is updated only if an administrator owns it; here, with the real list of trusted owners, the
        // test's own user isn't one.
        var shared = pc.Installer.DefaultFolder(Scope.Machine);
        await pc.Job.InstallAsync(shared, Scope.Machine, payload, null, null, Never, None);
        Assert.Null(pc.Installer.Problem(shared, null, Scope.Machine));
        var strict = new Installer(pc.Places with { TrustedOwners = [FolderSecurity.Administrators, FolderSecurity.LocalSystem] }, pc.Log);
        Assert.Equal(string.Format(SetupStrings.CouldBeChangedByOtherUsersChoose, shared), strict.Problem(shared, null, Scope.Machine));
        var untrusted = Assert.Throws<SetupException>(() => strict.Install(shared, Scope.Machine, payload, null, None));
        Assert.Equal(string.Format(SetupStrings.CouldBeChangedByOtherUsersChoose, shared), untrusted.Message);
    }

    [Fact]
    public async Task Uninstalling_never_goes_through_a_junction()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);

        // The shelf's pictures moved elsewhere with a junction, as someone short of room might.
        var pictures = Directory.CreateDirectory(pc.PathOf("My pictures")).FullName;
        File.WriteAllText(Path.Combine(pictures, "held.png"), "picture");
        Directory.CreateDirectory(pc.Places.DataFolder);
        var clipboard = Path.Combine(pc.Places.DataFolder, "Clipboard");
        SetupSandbox.Junction(clipboard, pictures);
        // A folder inside the install swapped for a junction to someone else's, holding a file with a listed name.
        var theirs = Directory.CreateDirectory(pc.PathOf("Theirs")).FullName;
        File.WriteAllText(Path.Combine(theirs, "LICENSE.txt"), "theirs");
        File.WriteAllText(Path.Combine(theirs, "other.txt"), "theirs too");
        Folders.DeleteOwn(Path.Combine(folder, "licenses"));
        SetupSandbox.Junction(Path.Combine(folder, "licenses"), theirs);

        Assert.Empty(await pc.Job.UninstallAsync(folder, running: null, null, Never, None));

        Assert.Equal("picture", Text(Path.Combine(pictures, "held.png")));
        Assert.False(Folders.IsLink(clipboard));
        Assert.False(Directory.Exists(pc.Places.DataFolder));
        Assert.Equal(["LICENSE.txt", "other.txt"], Directory.GetFiles(theirs).Select(Path.GetFileName).Order());
        Assert.Equal("theirs", Text(Path.Combine(theirs, "LICENSE.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "Tanjelo.exe")));
        // The junction isn't setup's, so it stays, and with it the folder; Windows' entries go.
        Assert.True(Folders.IsLink(Path.Combine(folder, "licenses")));
        Assert.Null(pc.AppsEntry(Scope.User));
        Assert.Contains("is a link to somewhere else", File.ReadAllText(pc.PathOf("setup.log")));
    }

    [Fact]
    public async Task An_update_stops_at_a_junction_inside_the_folder_and_its_own_folders_go_as_links()
    {
        using var pc = new SetupSandbox();
        var folder = pc.Installer.DefaultFolder(Scope.User);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.2.0", SetupSandbox.App("1.2.0")), null, null, Never, None);

        var theirs = Directory.CreateDirectory(pc.PathOf(@"Theirs\win-x64\native")).FullName;
        File.WriteAllText(Path.Combine(theirs, "WebView2Loader.dll"), "theirs");
        Folders.DeleteOwn(Path.Combine(folder, "runtimes"));
        SetupSandbox.Junction(Path.Combine(folder, "runtimes"), pc.PathOf("Theirs"));
        var stopped = await Assert.ThrowsAsync<SetupException>(() =>
            pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.3.0", SetupSandbox.App("1.3.0")), pc.Installer.Find(), null, Never, None));
        Assert.Equal(string.Format(SetupStrings.IsALinkToAnotherFolderSo, Path.Combine(folder, "runtimes")), stopped.Message);
        Assert.Equal("theirs", Text(Path.Combine(theirs, "WebView2Loader.dll")));
        Assert.Equal("Tanjelo 1.2.0", Text(Path.Combine(folder, "Tanjelo.exe")));

        // Setup's own working folders, should one be a junction left there, go as links, with nothing they point to.
        Directory.Delete(Path.Combine(folder, "runtimes"));
        var decoy = Directory.CreateDirectory(pc.PathOf("Decoy")).FullName;
        File.WriteAllText(Path.Combine(decoy, "keep.txt"), "keep");
        SetupSandbox.Junction(Path.Combine(folder, "~setup-new"), decoy);
        SetupSandbox.Junction(Path.Combine(folder, "~setup-old"), decoy);
        await pc.Job.InstallAsync(folder, Scope.User, pc.Payload("1.3.0", SetupSandbox.App("1.3.0")), pc.Installer.Find(), null, Never, None);
        Assert.Equal("Tanjelo 1.3.0", Text(Path.Combine(folder, "Tanjelo.exe")));
        Assert.Equal(["keep.txt"], Directory.GetFiles(decoy).Select(Path.GetFileName));
        Assert.False(Directory.Exists(Path.Combine(folder, "~setup-new")));
        Assert.False(Directory.Exists(Path.Combine(folder, "~setup-old")));
    }

    [Fact]
    public void Deleting_a_folder_of_setups_own_takes_links_in_it_as_links()
    {
        using var scratch = new Scratch();
        var target = Directory.CreateDirectory(scratch.PathOf("Target")).FullName;
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        var own = Directory.CreateDirectory(scratch.PathOf(@"Own\inner")).FullName;
        File.WriteAllText(Path.Combine(own, "mine.txt"), "mine");
        SetupSandbox.Junction(Path.Combine(own, "link"), target);
        SetupSandbox.Junction(scratch.PathOf("Link to target"), target);
        Assert.Equal(Path.Combine(own, "link"), Folders.LinkOnTheWay(scratch.Dir, Path.Combine(own, "link", "keep.txt")));
        Assert.Null(Folders.LinkOnTheWay(scratch.Dir, Path.Combine(own, "mine.txt")));

        Folders.DeleteOwn(scratch.PathOf("Own"));
        Folders.DeleteOwn(scratch.PathOf("Link to target"));

        Assert.False(Directory.Exists(scratch.PathOf("Own")));
        Assert.False(Directory.Exists(scratch.PathOf("Link to target")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.txt")));
    }
}
