using System.Security.AccessControl;
using System.Security.Principal;

namespace Tanjelo.Setup;

/// <summary>
/// Who may change a folder Tanjelo is installed in for everyone. Its folder is on the machine's PATH and run by every
/// user, administrators too, so only administrators may change it, as with Program Files: the folder is made with
/// Administrators as its owner and an access list of its own, not the one it would inherit. A folder made under C:\ or
/// on another drive would otherwise inherit "Authenticated Users: Modify", letting any user replace Tanjelo.exe.
/// </summary>
static class FolderSecurity
{
    public static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    public static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    static readonly SecurityIdentifier AllApplicationPackages = new("S-1-15-2-1");
    static readonly SecurityIdentifier AllRestrictedApplicationPackages = new("S-1-15-2-2");

    /// <summary>
    /// Program Files' rules: SYSTEM, Administrators and <paramref name="owner"/> may change it; users and Store apps may
    /// read and run what's in it. Nothing comes from the folder above.
    /// </summary>
    public static DirectorySecurity ForEveryone(SecurityIdentifier owner)
    {
        const InheritanceFlags Everything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var full in new[] { LocalSystem, Administrators, owner }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(full, FileSystemRights.FullControl, Everything, PropagationFlags.None, AccessControlType.Allow));
        foreach (var reader in new[] { Users, AllApplicationPackages, AllRestrictedApplicationPackages })
            security.AddAccessRule(new FileSystemAccessRule(reader, FileSystemRights.ReadAndExecute, Everything, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    /// <summary>Makes <paramref name="folder"/> with those rules from the start, or puts them back on the one that's there.</summary>
    public static void Secure(string folder, SecurityIdentifier owner)
    {
        var security = ForEveryone(owner);
        try
        {
            if (Directory.Exists(folder))
            {
                new DirectoryInfo(folder).SetAccessControl(security);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
#if NETFRAMEWORK
            Directory.CreateDirectory(folder, security);
#else
            FileSystemAclExtensions.CreateDirectory(security, folder);
#endif
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or PrivilegeNotHeldException or IOException)
        {
            throw new SetupException(string.Format(Strings.CouldnTMakeOnlyAdministratorsAble, folder, ex.Message));
        }
    }

    /// <summary>The folder's owner, or null if it can't be read.</summary>
    public static SecurityIdentifier? Owner(string folder)
    {
        try
        {
            return new DirectoryInfo(folder).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException or IdentityNotMappedException)
        {
            return null;
        }
    }
}
