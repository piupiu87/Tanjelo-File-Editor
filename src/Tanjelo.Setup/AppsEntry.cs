using Microsoft.Win32;

namespace Tanjelo.Setup;

/// <summary>A Tanjelo found on this PC, from its entry in Apps.</summary>
/// <param name="FromVelopack">Tanjelo 1.0 or 1.1, installed by Velopack in %LOCALAPPDATA%\Tanjelo, which setup moves over.</param>
sealed record Installed(Scope Scope, string Folder, string Version, bool FromVelopack);

/// <summary>
/// Tanjelo's entry in Settings → Apps, under the key name "Tanjelo" in either scope: the same key Velopack used, so
/// winget sees the same product before and after.
/// </summary>
static class AppsEntry
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Tanjelo";

    public static void Write(Places places, Scope scope, string folder, string version, long bytes)
    {
        var uninstaller = $"\"{Path.Combine(folder, Installer.UninstallerName)}\"";
        places.Root(scope).DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        using var key = places.Root(scope).CreateSubKey(KeyPath);
        key.SetValue("DisplayName", "Tanjelo");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "piupiu87");
        key.SetValue("DisplayIcon", $"\"{Path.Combine(folder, Installer.AppName)}\",0");
        key.SetValue("InstallLocation", folder);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        key.SetValue("UninstallString", uninstaller);
        key.SetValue("QuietUninstallString", uninstaller + " --silent");
        key.SetValue("URLInfoAbout", "https://github.com/piupiu87/Tanjelo-File-Editor");
        key.SetValue("HelpLink", "https://github.com/piupiu87/Tanjelo-File-Editor/issues");
        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    /// <summary>The Tanjelo installed in this scope, if its entry is there.</summary>
    public static Installed? Read(Places places, Scope scope)
    {
        using var key = places.Root(scope).OpenSubKey(KeyPath);
        if (key is null) return null;
        var uninstall = key.GetValue("UninstallString") as string ?? "";
        var folder = key.GetValue("InstallLocation") as string ?? "";
        var version = key.GetValue("DisplayVersion") as string ?? "";
        // Velopack's entry runs its Update.exe, in the root of the install, with --uninstall.
        var velopack = uninstall.IndexOf("Update.exe", StringComparison.OrdinalIgnoreCase) >= 0;
        if (velopack && folder.Length == 0)
            folder = Path.GetDirectoryName(uninstall.Trim().Split('"').FirstOrDefault(p => p.EndsWith("Update.exe", StringComparison.OrdinalIgnoreCase)) ?? "") ?? "";
        return folder.Length == 0 ? null : new Installed(scope, Folders.Trim(folder), version, velopack);
    }

    public static void Delete(Places places, Scope scope) => places.Root(scope).DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
}
