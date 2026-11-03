namespace Tanjelo.Setup;

/// <summary>
/// A whole install or uninstall, as the user, around the <see cref="Installer"/>: Tanjelo is asked to quit first, the
/// copying and registering for everyone goes to an elevated copy of setup, and the steps that belong to the user (moving
/// a 1.1 install over, Tanjelo's own clean-up, its data) stay with the user. The wizard and the silent command line
/// both go through here.
/// </summary>
sealed class SetupJob(Installer installer, ITanjeloProcesses tanjelo, IElevation elevation, Log log)
{
    /// <summary>How long Tanjelo has to quit when asked, and to stop when stopped.</summary>
    public TimeSpan QuitWait { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan StopWait { get; init; } = TimeSpan.FromSeconds(10);

    public Installer Installer => installer;

    /// <summary>
    /// Asks Tanjelo running from <paramref name="folder"/> to quit, and waits. If it's still running,
    /// <paramref name="mayStop"/> decides whether it's stopped. False if it's still running after all that.
    /// </summary>
    public bool CloseTanjelo(string folder, Func<bool> mayStop)
    {
        if (!Directory.Exists(folder) || tanjelo.In(folder).Count == 0) return true;
        log.Write($"asking Tanjelo in {folder} to quit");
        tanjelo.AskToQuit(folder);
        if (Gone(folder, QuitWait)) return true;
        if (!mayStop()) return false;
        foreach (var id in tanjelo.In(folder)) tanjelo.Stop(id, folder);
        return Gone(folder, StopWait);
    }

    bool Gone(string folder, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        while (tanjelo.In(folder).Count > 0)
        {
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(200);
        }
        return true;
    }

    /// <summary>Installs or updates Tanjelo in <paramref name="folder"/>, and moves <paramref name="existing"/> over if it's a 1.1 install.</summary>
    public async Task InstallAsync(string folder, Scope scope, Payload payload, Installed? existing, IProgress<Report>? progress,
        Func<bool> mayStop, CancellationToken cancel)
    {
        // Tanjelo can't be replaced while it runs: the copy being updated, or the 1.1 copy being moved over.
        var running = existing is { FromVelopack: true } ? existing.Folder : folder;
        if (!await Task.Run(() => CloseTanjelo(running, mayStop)).ConfigureAwait(false))
            throw new SetupException(Strings.TanjeloIsStillRunningCloseItFrom);
        log.Write($"installing Tanjelo {payload.Version} in {folder} ({(scope == Scope.Machine ? "everyone" : "this user")})");
        if (scope == Scope.Machine && !elevation.IsElevated)
            await elevation.RunAsync(new HelperRequest(HelperRequest.Install, folder), progress, cancel).ConfigureAwait(false);
        else
            await Task.Run(() => installer.Install(folder, scope, payload, progress, cancel)).ConfigureAwait(false);
        if (existing is { FromVelopack: true })
        {
            progress?.Report(new Report(Step.MovingOver));
            await Task.Run(() => VelopackInstall.MoveOver(installer.Places, existing, Path.Combine(folder, Installer.AppName), log)).ConfigureAwait(false);
        }
        log.Write("installed");
    }

    /// <summary>
    /// Uninstalls the Tanjelo in <paramref name="folder"/>. <paramref name="running"/> is the uninstaller in use, which
    /// goes once it has exited. Returns the files that couldn't be removed.
    /// </summary>
    public async Task<IReadOnlyList<string>> UninstallAsync(string folder, string? running, IProgress<Report>? progress, Func<bool> mayStop, CancellationToken cancel)
    {
        var list = Installer.ReadList(folder) ?? throw new SetupException(string.Format(Strings.TanjeloIsnTInstalledIn, folder));
        if (!await Task.Run(() => CloseTanjelo(folder, mayStop)).ConfigureAwait(false))
            throw new SetupException(Strings.TanjeloIsStillRunningCloseItFrom);
        log.Write($"uninstalling Tanjelo {list.Version} from {folder}");
        // Start with Windows, Send to and the Windows 11 menu entry are this user's, so Tanjelo takes them away as the user.
        await Task.Run(() => tanjelo.BeforeUninstall(Path.Combine(folder, Installer.AppName))).ConfigureAwait(false);
        var left = list.Scope == Scope.Machine && !elevation.IsElevated
            ? await elevation.RunAsync(new HelperRequest(HelperRequest.Uninstall, folder, running), progress, cancel).ConfigureAwait(false)
            : await Task.Run(() => installer.Uninstall(folder, running, progress)).ConfigureAwait(false);
        if (left.Count == 0)
        {
            await Task.Run(installer.RemoveData).ConfigureAwait(false);
            log.Write("uninstalled");
        }
        return left;
    }

    /// <summary>Starts the installed Tanjelo, as the user.</summary>
    public void Start(string folder) => tanjelo.Start(Path.Combine(folder, Installer.AppName));
}
