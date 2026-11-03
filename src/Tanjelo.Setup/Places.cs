using System.Security.Principal;
using Microsoft.Win32;

namespace Tanjelo.Setup;

/// <summary>"Just for me" or "everyone on this PC".</summary>
enum Scope { User, Machine }

/// <summary>
/// Where setup reads and writes: folders and registry roots. <see cref="ForWindows"/> gives the real ones; the tests
/// point every one of them into a scratch folder and a scratch registry key, so they never touch this PC's PATH, Start
/// menu or Apps list.
/// </summary>
sealed record Places
{
    /// <summary>%LOCALAPPDATA%: "just for me" installs go in its Programs folder, Tanjelo keeps its data in its Tanjelo
    /// folder, and Tanjelo 1.1 was installed there too.</summary>
    public string LocalAppData { get; init; } = "";

    /// <summary>%APPDATA%, where Tanjelo keeps its settings.</summary>
    public string RoamingAppData { get; init; } = "";

    public string ProgramFiles { get; init; } = "";

    /// <summary>The user's own Start menu programs folder, and the one everyone on the PC sees.</summary>
    public string UserStartMenu { get; init; } = "";
    public string CommonStartMenu { get; init; } = "";

    public string SendTo { get; init; } = "";
    public string Desktop { get; init; } = "";

    /// <summary>The shortcuts behind the user's taskbar pins.</summary>
    public string TaskbarPins { get; init; } = "";

    public string Temp { get; init; } = "";
    public string Windows { get; init; } = "";

    public RegistryKey CurrentUser { get; init; } = null!;
    public RegistryKey LocalMachine { get; init; } = null!;

    /// <summary>Whether to tell running programs that the PATH changed, as Windows expects; never from the tests.</summary>
    public bool Announce { get; init; }

    /// <summary>
    /// The owner of a folder installed for everyone: Administrators, as for Program Files. The tests, which can't make
    /// anyone but themselves an owner, use their own user.
    /// </summary>
    public SecurityIdentifier MachineOwner { get; init; } = FolderSecurity.Administrators;

    /// <summary>The owners an existing folder for everyone may have: ones only an administrator could have given it.</summary>
    public IReadOnlyList<SecurityIdentifier> TrustedOwners { get; init; } = [FolderSecurity.Administrators, FolderSecurity.LocalSystem];

    public static Places ForWindows()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new Places
        {
            LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            RoamingAppData = roaming,
            // The 64-bit Program Files, even if this ever runs as a 32-bit process.
            ProgramFiles = Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } x64 ? x64 : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            UserStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            CommonStartMenu = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            SendTo = Environment.GetFolderPath(Environment.SpecialFolder.SendTo),
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            TaskbarPins = Path.Combine(roaming, "Microsoft", "Internet Explorer", "Quick Launch", "User Pinned", "TaskBar"),
            Temp = Path.GetTempPath(),
            Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            CurrentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
            LocalMachine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
            Announce = true,
        };
    }

    /// <summary>Tanjelo's data: its log, the update check's answer, pictures held on the shelf, WebView2's cache.</summary>
    public string DataFolder => Path.Combine(LocalAppData, "Tanjelo");

    /// <summary>Tanjelo's settings, which stay when it's uninstalled, in case it comes back.</summary>
    public string SettingsFolder => Path.Combine(RoamingAppData, "Tanjelo");

    /// <summary>The folder Tanjelo's own folder goes in by default.</summary>
    public string DefaultParent(Scope scope) => scope == Scope.User ? Path.Combine(LocalAppData, "Programs") : ProgramFiles;

    public RegistryKey Root(Scope scope) => scope == Scope.User ? CurrentUser : LocalMachine;

    public string StartMenu(Scope scope) => scope == Scope.User ? UserStartMenu : CommonStartMenu;
}
