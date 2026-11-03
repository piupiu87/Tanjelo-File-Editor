using Microsoft.Win32;

namespace Tanjelo.Setup;

/// <summary>
/// A program Tanjelo uses if it's there, which setup offers to install with winget: never bundled, so the GPL ones
/// (ffmpeg, pandoc) stay programs of their own. The HEIC extensions come from the Microsoft Store instead, since one
/// is paid, so ticking them opens their Store pages.
/// </summary>
/// <param name="UserScope">Whether winget can install it just for the user; the others ask for administrator permission.</param>
/// <param name="TickedWhenMissing">Ticked at first when it's missing: the tools most of Tanjelo needs.</param>
sealed record OptionalTool(string Key, string Name, string Purpose, string Size, string Note, string? WingetId, bool UserScope,
    string Site, string Licence, bool TickedWhenMissing, string[] StorePages);

static class OptionalTools
{
    public const string HeifStoreId = "9PMMSR1CGPWG", HevcStoreId = "9NMZLZ57R3T7";

    public static IReadOnlyList<OptionalTool> All =>
    [
        new("ffmpeg", "ffmpeg", Strings.FfmpegPurpose, Strings.About100MB, Strings.GplInstalledAsItsOwnProgram, "Gyan.FFmpeg.Essentials", false,
            "https://www.gyan.dev/ffmpeg/builds/", "https://ffmpeg.org/legal.html", true, []),
        new("pandoc", "pandoc", Strings.PandocPurpose, Strings.About40MB, Strings.GplInstalledAsItsOwnProgram, "JohnMacFarlane.Pandoc", true,
            "https://pandoc.org/installing.html", "https://github.com/jgm/pandoc/blob/main/COPYRIGHT", true, []),
        new("webview2", Strings.WebView2Runtime, Strings.WebView2Purpose, Strings.About180MB, Strings.MicrosoftSLicenceTerms, "Microsoft.EdgeWebView2Runtime", true,
            "https://developer.microsoft.com/microsoft-edge/webview2/", "https://developer.microsoft.com/microsoft-edge/webview2/", true, []),
        new("7zip", "7-Zip", Strings.SevenZipPurpose, Strings.About2MB, Strings.AsksForAdministratorPermission, "7zip.7zip", false,
            "https://www.7-zip.org/", "https://www.7-zip.org/license.txt", false, []),
        new("libreoffice", "LibreOffice", Strings.LibreOfficePurpose, Strings.About350MB, Strings.LargeAsksForAdministratorPermission, "TheDocumentFoundation.LibreOffice", false,
            "https://www.libreoffice.org/download/download-libreoffice/", "https://www.libreoffice.org/about-us/licenses/", false, []),
        new("heic", Strings.HeicExtensions, Strings.HeicPurpose, Strings.AFewMB, Strings.FromTheMicrosoftStoreHevcCosts, null, false,
            "https://apps.microsoft.com/detail/" + HeifStoreId, "https://apps.microsoft.com/detail/" + HevcStoreId, false,
            ["ms-windows-store://pdp/?ProductId=" + HeifStoreId, "ms-windows-store://pdp/?ProductId=" + HevcStoreId]),
    ];

    /// <summary>
    /// Whether Tanjelo would find the tool, looking where Tanjelo.Core's Toolbox.Detect looks (a test keeps the two in
    /// step), except for a path chosen in Tanjelo's settings.
    /// </summary>
    public static bool IsInstalled(OptionalTool tool) => tool.Key switch
    {
        "ffmpeg" => FirstExisting([OnPath("ffmpeg.exe"), .. WinGetPackages("Gyan.FFmpeg", "ffmpeg.exe"), @"C:\ffmpeg\bin\ffmpeg.exe"]) is not null,
        "pandoc" => FirstExisting([OnPath("pandoc.exe"), .. WinGetPackages("JohnMacFarlane.Pandoc", "pandoc.exe"),
            Path.Combine(LocalAppData, "Pandoc", "pandoc.exe"), Path.Combine(ProgramFiles, "Pandoc", "pandoc.exe")]) is not null,
        "7zip" => FirstExisting([Path.Combine(ProgramFiles, "7-Zip", "7z.exe"), Path.Combine(ProgramFilesX86, "7-Zip", "7z.exe"), OnPath("7z.exe")]) is not null,
        "libreoffice" => FirstExisting([Path.Combine(ProgramFiles, "LibreOffice", "program", "soffice.exe"),
            Path.Combine(ProgramFilesX86, "LibreOffice", "program", "soffice.exe")]) is not null,
        "webview2" => WebView2Version() is not null,
        "heic" => HasPackage("Microsoft.HEIFImageExtension_") && HasPackage("Microsoft.HEVCVideoExtension"),
        _ => false,
    };

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string ProgramFiles => Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 } x64 ? x64 : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    static string? FirstExisting(IEnumerable<string?> candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));

    /// <summary>The tool on the PATH, the saved user and machine ones too: one installed a moment ago isn't in ours yet.</summary>
    static string? OnPath(string exe)
    {
        var folders = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .SelectMany(target => (Environment.GetEnvironmentVariable("PATH", target) ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries));
        foreach (var folder in folders)
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim()), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* a malformed PATH entry */ }
        }
        return null;
    }

    static IEnumerable<string> WinGetPackages(string prefix, string exe)
    {
        yield return Path.Combine(LocalAppData, "Microsoft", "WinGet", "Links", exe);
        var packages = Path.Combine(LocalAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var package in Directory.EnumerateDirectories(packages, prefix + "*"))
            foreach (var found in Directory.EnumerateFiles(package, exe, SearchOption.AllDirectories))
                yield return found;
    }

    /// <summary>The WebView2 runtime's version from its EdgeUpdate key, for everyone or just this user, as Microsoft says to check.</summary>
    static string? WebView2Version()
    {
        const string Client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (hive, view, key) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry32, @"SOFTWARE\" + Client),
                     (RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\" + Client),
                     (RegistryHive.CurrentUser, RegistryView.Default, @"Software\" + Client),
                 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var client = root.OpenSubKey(key);
            if (client?.GetValue("pv") is string version && version.Length > 0 && version != "0.0.0.0") return version;
        }
        return null;
    }

    /// <summary>Whether a Store package whose name starts so is installed for this user.</summary>
    static bool HasPackage(string prefix)
    {
        using var packages = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        return packages?.GetSubKeyNames().Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
