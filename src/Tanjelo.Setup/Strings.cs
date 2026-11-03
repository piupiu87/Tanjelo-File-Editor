using System.Resources;
using System.Runtime.CompilerServices;

namespace Tanjelo.Setup;

/// <summary>Setup's words, kept in Strings.resx like the app's, so they can be translated.</summary>
public static class Strings
{
    static readonly ResourceManager Resources = new("Tanjelo.Setup.Strings", typeof(Strings).Assembly);

    /// <summary>Changes every string as it's read: the pseudo-language test brackets and lengthens them all.</summary>
    internal static Func<string, string>? Transform { get; set; }

    static string Get([CallerMemberName] string key = "")
    {
        var text = Resources.GetString(key) ?? key;
        return Transform is { } transform ? transform(text) : text;
    }

    public static string SetupSOwnCopyOfTanjeloIsDamaged => Get();
    public static string ChooseAFolderOnThisPC => Get();
    public static string ThatIsnTAFolderSetupCanUse => Get();
    public static string ChooseAFolderNotAWholeDrive => Get();
    public static string ChooseAFolderOutsideTheWindowsFolder => Get();
    public static string TanjeloKeepsItsOwnFilesInChoose => Get();
    public static string AlreadyHasFilesInItChooseAn => Get();
    public static string SetupCanTWriteToForAFolder => Get();
    public static string CouldnTCopyTanjelo => Get();
    public static string CouldnTReplaceTanjeloSFiles => Get();
    public static string ConvertAndEditFilesStraightFrom => Get();
    public static string CouldnTAddTanjeloToWindows => Get();
    public static string TanjeloIsnTInstalledIn => Get();
    public static string CouldnTRemoveTanjeloFromWindows => Get();
    public static string InstallingForEveryoneNeedsAdministratorPermission => Get();
    public static string TheAdministratorCopyOfSetupStopped => Get();
    public static string TanjeloIsStillRunningCloseItFrom => Get();
    public static string NeedsAValue => Get();
    public static string SetupDoesnTKnow => Get();
}
