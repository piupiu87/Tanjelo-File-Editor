namespace Tanjelo.Core;

/// <summary>External programs the converters can use. Anything missing just removes its formats from the menu.</summary>
public sealed class Toolbox
{
    public string? Ffmpeg { get; init; }
    public string? Ffprobe { get; init; }
    public string? SevenZip { get; init; }
    public string? Tar { get; init; }
    public string? LibreOffice { get; init; }
    public bool Word { get; init; }
    public bool PowerPoint { get; init; }
    public bool Excel { get; init; }

    /// <summary>Windows Terminal's wt.exe, usually an app execution alias in %LOCALAPPDATA%MicrosoftWindowsApps.</summary>
    public string? WindowsTerminal { get; init; }

    /// <summary>The version of the WebView2 runtime (Edge's engine, part of Windows 11), which prints HTML to PDF.</summary>
    public string? WebView2 { get; init; }

    /// <summary>pandoc.exe, for Word and EPUB documents. GPL-licensed, so only ever run as the program the user installed.</summary>
    public string? Pandoc { get; init; }

    public static Toolbox Detect(string? ffmpegPath = null, string? sevenZipPath = null)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var ffmpeg = FirstExisting([ffmpegPath, .. Bundled("ffmpeg.exe"), FindOnPath("ffmpeg.exe"), .. WinGetPackages("Gyan.FFmpeg", "ffmpeg.exe"), @"C:\ffmpeg\bin\ffmpeg.exe"]);
        var ffprobe = ffmpeg is null ? null : FirstExisting([Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe")]);

        return new Toolbox
        {
            Ffmpeg = ffmpeg,
            Ffprobe = ffprobe,
            SevenZip = FirstExisting([sevenZipPath, .. Bundled("7z.exe"), Path.Combine(programFiles, "7-Zip", "7z.exe"), Path.Combine(programFilesX86, "7-Zip", "7z.exe"), FindOnPath("7z.exe")]),
            Tar = FirstExisting([Path.Combine(Environment.SystemDirectory, "tar.exe")]),
            LibreOffice = FirstExisting([Path.Combine(programFiles, "LibreOffice", "program", "soffice.exe"), Path.Combine(programFilesX86, "LibreOffice", "program", "soffice.exe")]),
            Word = Type.GetTypeFromProgID("Word.Application") is not null,
            PowerPoint = Type.GetTypeFromProgID("PowerPoint.Application") is not null,
            Excel = Type.GetTypeFromProgID("Excel.Application") is not null,
            WindowsTerminal = FirstExisting([Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe"), FindOnPath("wt.exe")]),
            WebView2 = Converters.HtmlPrinter.RuntimeVersion(),
            // winget's MSI installs per user into %LOCALAPPDATA%\Pandoc; the machine-wide MSI into Program Files.
            Pandoc = FirstExisting([FindOnPath("pandoc.exe"), .. WinGetPackages("JohnMacFarlane.Pandoc", "pandoc.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pandoc", "pandoc.exe"),
                Path.Combine(programFiles, "Pandoc", "pandoc.exe")]),
        };
    }

    static string? FirstExisting(IEnumerable<string?> candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));

    static IEnumerable<string> Bundled(string exe)
    {
        yield return Path.Combine(AppContext.BaseDirectory, exe);
        yield return Path.Combine(AppContext.BaseDirectory, "tools", exe);
    }

    // Reads the persisted user/machine PATH too: a tool installed after we started isn't in our own environment yet.
    static string? FindOnPath(string exe)
    {
        var dirs = new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(t => Environment.GetEnvironmentVariable("PATH", t) ?? "")
            .SelectMany(p => p.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var dir in dirs)
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { /* malformed PATH entry */ }
        }
        return null;
    }

    static IEnumerable<string> WinGetPackages(string packagePrefix, string exe)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", exe);
        var packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(packages)) yield break;
        foreach (var package in Directory.EnumerateDirectories(packages, packagePrefix + "*"))
            foreach (var found in Directory.EnumerateFiles(package, exe, SearchOption.AllDirectories))
                yield return found;
    }
}
