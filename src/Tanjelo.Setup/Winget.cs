using System.Diagnostics;
using System.Text;

namespace Tanjelo.Setup;

/// <summary>
/// Installs optional tools with winget, Windows' package manager, as the user: silently, accepting the package's and
/// the source's agreements (the tools page says so), just for the user where the package can be. Nothing is downloaded
/// from anywhere but winget's own source. The tests use a fake winget that succeeds, fails or hangs.
/// </summary>
sealed class Winget(string exe, Log log)
{
    /// <summary>How long one tool may take; LibreOffice is large.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);

    public string Exe => exe;

    /// <summary>winget on this PC, or null where it isn't (some Windows 10 PCs, LTSC, Windows Sandbox).</summary>
    public static string? Find()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(alias)) return alias;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split([';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(folder => Path.Combine(folder.Trim(), "winget.exe")).FirstOrDefault(File.Exists);
    }

    public static string Arguments(OptionalTool tool) =>
        $"install --id {tool.WingetId} --exact --source winget --silent --accept-package-agreements --accept-source-agreements --disable-interactivity"
        + (tool.UserScope ? " --scope user" : "");

    // winget's own codes for "it's there already", which count as done.
    const int UpdateNotApplicable = unchecked((int)0x8A15002B);
    const int PackageAlreadyInstalled = unchecked((int)0x8A150061);

    /// <summary>Installs <paramref name="tool"/>; null when it's done, otherwise why not, in words fit to show.</summary>
    public async Task<string?> InstallAsync(OptionalTool tool, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(exe, Arguments(tool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.SystemDirectory,
        };
        log.Write($"winget {start.Arguments}");
        Process? run;
        try
        {
            run = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return string.Format(Strings.WingetCouldnTStart, ex.Message);
        }
        if (run is null) return string.Format(Strings.WingetCouldnTStart, "");
        using (run)
        {
            var output = run.StandardOutput.ReadToEndAsync();
            var errors = run.StandardError.ReadToEndAsync();
            // If winget is stopped, its output is never read to the end, and whatever that ends in doesn't matter.
            ProcessExit.Ignore(output);
            ProcessExit.Ignore(errors);
            var exited = ProcessExit.Of(run);
            var skipped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancel.Register(() => skipped.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(exited, skipped.Task, Task.Delay(Timeout)).ConfigureAwait(false);
                if (finished != exited)
                {
                    Stop(run);
                    var why = finished == skipped.Task ? Strings.Skipped : string.Format(Strings.TookLongerThanMinutes, Math.Max(1, (int)Math.Ceiling(Timeout.TotalMinutes)));
                    log.Write($"winget {tool.WingetId}: {why}");
                    return why;
                }
            }
            run.WaitForExit();
            var text = await output.ConfigureAwait(false) + await errors.ConfigureAwait(false);
            log.Write($"winget {tool.WingetId} ended with {run.ExitCode:X8}: {LastLine(text)}");
            return run.ExitCode is 0 or UpdateNotApplicable or PackageAlreadyInstalled ? null
                : LastLine(text) is { Length: > 0 } line ? line
                : string.Format(Strings.WingetEndedWithCode, run.ExitCode.ToString("X8"));
        }
    }

    static void Stop(Process run)
    {
        try
        {
#if NETFRAMEWORK
            run.Kill();
#else
            run.Kill(entireProcessTree: true);
#endif
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    /// <summary>The last line winget wrote that says something: not its spinner or progress bars.</summary>
    internal static string LastLine(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .LastOrDefault(line => line.Any(char.IsLetter) && line.IndexOfAny(['█', '▒']) < 0) ?? "";
}
