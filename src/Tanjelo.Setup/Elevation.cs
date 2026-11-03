using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Tanjelo.Setup;

/// <summary>What the elevated copy of setup is asked to do for everyone on the PC: copy and register, or remove.</summary>
sealed record HelperRequest(string Job, string Folder, string? Running = null)
{
    public const string Install = "install", Uninstall = "uninstall";

    public IEnumerable<string> Arguments(string pipe, string log)
    {
        string[] arguments = ["--elevated", Job, "--pipe", pipe, "--dir", Folder, "--log", log];
        return Running is null ? arguments : arguments.Concat(["--running", Running]);
    }

    /// <summary>Does the job with <paramref name="installer"/>; returns the files an uninstall couldn't remove.</summary>
    public IReadOnlyList<string> Run(Installer installer, Payload? payload, IProgress<Report> progress, CancellationToken cancel)
    {
        if (Job == Uninstall) return installer.Uninstall(Folder, Running, progress);
        installer.Install(Folder, Scope.Machine, payload ?? throw new SetupException(Strings.SetupSOwnCopyOfTanjeloIsDamaged), progress, cancel);
        return [];
    }
}

/// <summary>Running setup's job for everyone, which needs administrator permission.</summary>
interface IElevation
{
    /// <summary>Whether this setup already runs as administrator, so it can do the job itself.</summary>
    bool IsElevated { get; }

    /// <summary>
    /// Runs the job in a copy of setup started as administrator (one prompt), passing on its reports. Returns the files
    /// an uninstall couldn't remove; throws the copy's own reason when it fails.
    /// </summary>
    Task<IReadOnlyList<string>> RunAsync(HelperRequest request, IProgress<Report>? progress, CancellationToken cancel);
}

sealed class Elevation(string exe, Log log) : IElevation
{
    public bool IsElevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public async Task<IReadOnlyList<string>> RunAsync(HelperRequest request, IProgress<Report>? progress, CancellationToken cancel)
    {
        var name = "Tanjelo.Setup." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        Process? helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(exe, CommandLine.Join(request.Arguments(name, log.FilePath)))
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.SystemDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: the prompt was answered no
        {
            throw new SetupException(Strings.InstallingForEveryoneNeedsAdministratorPermission, ExitCodes.NoAdministrator);
        }
        if (helper is null) throw new SetupException(Strings.InstallingForEveryoneNeedsAdministratorPermission, ExitCodes.NoAdministrator);
        using (helper)
        {
            using var gone = new CancellationTokenSource();
            helper.EnableRaisingEvents = true;
            helper.Exited += (_, _) => gone.Cancel();
            if (helper.HasExited) gone.Cancel();
            try
            {
                await pipe.WaitForConnectionAsync(gone.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new SetupException(string.Format(Strings.TheAdministratorCopyOfSetupStopped, helper.ExitCode));
            }
            var left = await HelperChannel.ListenAsync(pipe, progress, cancel).ConfigureAwait(false);
            helper.WaitForExit();
            return left;
        }
    }
}

/// <summary>
/// How the elevated copy and the setup that started it talk, over a named pipe, one line at a time: reports and the
/// outcome one way, "cancel" the other.
/// </summary>
static class HelperChannel
{
    /// <summary>Setup's side: passes on the helper's reports until it says how it went.</summary>
    public static async Task<IReadOnlyList<string>> ListenAsync(Stream pipe, IProgress<Report>? progress, CancellationToken cancel)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        using var asking = cancel.Register(() =>
        {
            try { writer.WriteLine("cancel"); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        var left = new List<string>();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            var space = line.IndexOf(' ');
            var verb = space < 0 ? line : line.Substring(0, space);
            var rest = space < 0 ? "" : line.Substring(space + 1);
            switch (verb)
            {
                case "report":
                    var parts = rest.Split(' ');
                    if (parts.Length == 3 && Enum.TryParse<Step>(parts[0], out var step) && long.TryParse(parts[1], out var done) && long.TryParse(parts[2], out var total))
                        progress?.Report(new Report(step, done, total));
                    break;
                case "left":
                    left.Add(rest);
                    break;
                case "done":
                    return left;
                case "cancelled":
                    throw new OperationCanceledException(cancel);
                case "fail":
                    var codeEnd = rest.IndexOf(' ');
                    throw codeEnd > 0 && int.TryParse(rest.Substring(0, codeEnd), out var code)
                        ? new SetupException(rest.Substring(codeEnd + 1), code)
                        : new SetupException(rest);
            }
        }
        throw new SetupException(string.Format(Strings.TheAdministratorCopyOfSetupStopped, "?"));
    }

    /// <summary>The helper's side: does the job, sending its reports, and stops if asked to. Returns its exit code.</summary>
    public static int Serve(Stream pipe, Func<IProgress<Report>, CancellationToken, IReadOnlyList<string>> job)
    {
        var reader = new StreamReader(pipe, new UTF8Encoding(false));
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new object();
        void Send(string line)
        {
            lock (gate)
            {
                try { writer.WriteLine(line.Replace('\r', ' ').Replace('\n', ' ')); }
                catch (IOException) { /* setup has gone; the job carries on to its end */ }
            }
        }
        using var cancel = new CancellationTokenSource();
        var listening = new Thread(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
                    if (line == "cancel") cancel.Cancel();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }) { IsBackground = true };
        listening.Start();
        try
        {
            var left = job(new Reporter<Report>(r => Send($"report {r.Step} {r.Done} {r.Total}")), cancel.Token);
            foreach (var file in left) Send("left " + file);
            Send("done");
            return left.Count == 0 ? ExitCodes.Done : ExitCodes.Failed;
        }
        catch (OperationCanceledException)
        {
            Send("cancelled");
            return ExitCodes.Cancelled;
        }
        catch (SetupException ex)
        {
            Send($"fail {ex.ExitCode} {ex.Message}");
            return ex.ExitCode;
        }
    }
}

/// <summary>Command lines put together the way Windows programs take them apart again.</summary>
static class CommandLine
{
    public static string Join(IEnumerable<string> arguments) => string.Join(" ", arguments.Select(Quote));

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0) return argument;
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes count only before a quote, where each is doubled and the quote escaped.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
