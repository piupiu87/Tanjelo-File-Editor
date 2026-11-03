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
    public static string TanjeloSetup => Get();
    public static string UninstallTanjelo => Get();
    public static string TanjeloConvertsAndEditsFiles => Get();
    public static string TanjeloIsFreeAndOpenSource => Get();
    public static string MITLicence => Get();
    public static string TanjeloIsInstalledForYouIn => Get();
    public static string TanjeloIsInstalledForEveryoneIn => Get();
    public static string TanjeloIsInstalledInSetupMovesIt => Get();
    public static string InstallTanjeloFor => Get();
    public static string JustForMe => Get();
    public static string NoAdministratorPermissionNeeded => Get();
    public static string EveryoneOnThisPC => Get();
    public static string AsksForAdministratorPermissionOnce => Get();
    public static string Folder => Get();
    public static string Browse => Get();
    public static string ChooseWhereTanjeloGoes => Get();
    public static string TanjeloGoesInAFolderOfItsOwn => Get();
    public static string TanjeloIsInstalledInSoItSUpdated => Get();
    public static string SpaceNeeded => Get();
    public static string SpaceNeededFreeOn => Get();
    public static string Progress => Get();
    public static string OpenSetupSLog => Get();
    public static string StartTanjelo => Get();
    public static string YourSettingsStayInAppData => Get();
    public static string Back => Get();
    public static string Next => Get();
    public static string Cancel => Get();
    public static string Install => Get();
    public static string Update => Get();
    public static string Finish => Get();
    public static string Uninstall => Get();
    public static string InstallTanjelo => Get();
    public static string UpdateTanjelo => Get();
    public static string Version => Get();
    public static string WhereTanjeloGoes => Get();
    public static string InstallingTanjelo => Get();
    public static string UpdatingTanjelo => Get();
    public static string UninstallingTanjelo => Get();
    public static string UninstallTanjeloQuestion => Get();
    public static string CopyingTanjelo => Get();
    public static string CopyingTanjeloOf => Get();
    public static string CheckingSetupSCopyOfTanjelo => Get();
    public static string AddingTanjeloToTheStartMenu => Get();
    public static string MovingTanjeloOver => Get();
    public static string Cancelling => Get();
    public static string TanjeloIsStillRunningAndDidnT => Get();
    public static string TanjeloIsInstalled => Get();
    public static string TanjeloIsUpdated => Get();
    public static string TanjeloIsInStartItFrom => Get();
    public static string SetupWasCancelled => Get();
    public static string TanjeloWasnTInstalled => Get();
    public static string NothingWasInstalled => Get();
    public static string TanjeloStaysAsItWas => Get();
    public static string ThisRemovesTanjeloFrom => Get();
    public static string RemovingTanjelo => Get();
    public static string ClosingTanjelo => Get();
    public static string RemovingTanjeloSFilesOf => Get();
    public static string TanjeloIsUninstalled => Get();
    public static string TanjeloIsUninstalledThanks => Get();
    public static string TanjeloWasnTFullyUninstalled => Get();
    public static string TheseFilesCouldnTBeRemoved => Get();
    public static string StepWaiting => Get();
    public static string StepWorking => Get();
    public static string StepDone => Get();
    public static string StepFailed => Get();
    public static string StepSkipped => Get();
    public static string Steps => Get();
    public static string OptionalTools => Get();
    public static string TickedToolsAreInstalledWithWinget => Get();
    public static string WingetIsnTOnThisPC => Get();
    public static string FfmpegPurpose => Get();
    public static string PandocPurpose => Get();
    public static string WebView2Runtime => Get();
    public static string WebView2Purpose => Get();
    public static string SevenZipPurpose => Get();
    public static string LibreOfficePurpose => Get();
    public static string HeicExtensions => Get();
    public static string HeicPurpose => Get();
    public static string About100MB => Get();
    public static string About40MB => Get();
    public static string About180MB => Get();
    public static string About2MB => Get();
    public static string About350MB => Get();
    public static string AFewMB => Get();
    public static string GplInstalledAsItsOwnProgram => Get();
    public static string MicrosoftSLicenceTerms => Get();
    public static string AsksForAdministratorPermission => Get();
    public static string LargeAsksForAdministratorPermission => Get();
    public static string FromTheMicrosoftStoreHevcCosts => Get();
    public static string Installed => Get();
    public static string Licence => Get();
    public static string GetItFromItsSite => Get();
    public static string InstallingTool => Get();
    public static string OpeningTheMicrosoftStorePagesFor => Get();
    public static string AlsoInstalled => Get();
    public static string SkipTheRest => Get();
    public static string TheRestAreSkippedOnceThisOne => Get();
    public static string ToolsThatWerenTInstalled => Get();
    public static string WingetCouldnTStart => Get();
    public static string WingetEndedWithCode => Get();
    public static string TookLongerThanMinutes => Get();
    public static string Skipped => Get();
    public static string CouldnTMakeOnlyAdministratorsAble => Get();
    public static string ForEveryoneOnThisPCChooseANewFolder => Get();
    public static string CouldBeChangedByOtherUsersChoose => Get();
    public static string IsALinkToAnotherFolderSo => Get();
}
