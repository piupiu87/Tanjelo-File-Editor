using System.IO.Pipes;

namespace Tanjelo.Setup;

/// <summary>
/// Setup.exe, with the app inside, installs or updates Tanjelo; the same program without it is the uninstaller that
/// setup puts beside the app. Either can run silently, and either may start an elevated copy of itself to do the work
/// that needs administrator permission.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Setup never keeps the install folder open, so the folder can be removed.
        Environment.CurrentDirectory = Environment.SystemDirectory;
        var log = Log.Default();
        try
        {
            var options = Options.Parse(args);
            if (options.Log is { } path) log = new Log(path);
            var assembly = typeof(Program).Assembly;
            var payload = Payload.Embedded(assembly);
            var exe = assembly.Location;
            if (options.Elevated is not null) return RunElevated(options, payload, log);
            var installer = new Installer(Places.ForWindows(), log);
            var job = new SetupJob(installer, new TanjeloProcesses(log), new Elevation(exe, log), log);
            if (options.Silent) return payload is null ? Uninstall(job, exe) : Install(job, options, payload, log);
            var folder = Path.GetDirectoryName(exe)!;
            if (payload is null && Installer.ReadList(folder) is null) throw new SetupException(string.Format(Strings.TanjeloIsnTInstalledIn, folder));
            return Wizard(payload is null ? new SetupWindow(job, folder, exe, log) : new SetupWindow(job, payload, options, log));
        }
        catch (SetupException ex)
        {
            log.Write(ex.Message);
            if (!args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)) && !args.Contains("--elevated"))
                System.Windows.MessageBox.Show(ex.Message, "Tanjelo", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return ex.ExitCode;
        }
        catch (OperationCanceledException)
        {
            log.Write("cancelled");
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            log.Write(ex.ToString());
            return ExitCodes.Failed;
        }
    }

    static int Install(SetupJob job, Options options, Payload payload, Log log)
    {
        var installer = job.Installer;
        var existing = installer.Find();
        var (folder, scope, note) = installer.Choose(existing, options.AllUsers, options.Folder);
        if (note is not null) log.Write(note);
        if (installer.Problem(folder, existing) is { } problem) throw new SetupException(problem);
        job.InstallAsync(folder, scope, payload, existing, null, mayStop: () => true, CancellationToken.None).GetAwaiter().GetResult();
        return ExitCodes.Done;
    }

    /// <summary>Shows the wizard until it's closed, and ends with what it did.</summary>
    static int Wizard(SetupWindow window)
    {
        new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose }.Run(window);
        return window.ExitCode;
    }

    static int Uninstall(SetupJob job, string exe)
    {
        var folder = Path.GetDirectoryName(exe)!;
        var left = job.UninstallAsync(folder, exe, null, mayStop: () => true, CancellationToken.None).GetAwaiter().GetResult();
        return left.Count == 0 ? ExitCodes.Done : ExitCodes.Failed;
    }

    /// <summary>The elevated copy: does one job for everyone on the PC, reporting to the setup that started it.</summary>
    static int RunElevated(Options options, Payload? payload, Log log)
    {
        if (options.Pipe is null || options.Folder is null) throw new SetupException(string.Format(Strings.NeedsAValue, "--elevated"), ExitCodes.BadCommandLine);
        var request = new HelperRequest(options.Elevated!, options.Folder, options.Running);
        using var pipe = new NamedPipeClientStream(".", options.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect(30_000);
        var installer = new Installer(Places.ForWindows(), log);
        return HelperChannel.Serve(pipe, (progress, cancel) => request.Run(installer, payload, progress, cancel));
    }
}
