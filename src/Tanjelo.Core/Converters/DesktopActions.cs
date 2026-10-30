namespace Tanjelo.Core.Converters;

/// <summary>What only the app can do with a selection, because it needs windows: the Share sheet and the Float shelf.</summary>
public interface IDesktopActions
{
    /// <summary>Opens the Windows Share sheet for these files, near the pointer.</summary>
    Task ShareAsync(IReadOnlyList<string> files);

    /// <summary>Parks these files and folders on the Float shelf and shows it.</summary>
    Task FloatAsync(IReadOnlyList<string> paths);
}

/// <summary>Share… and Float on the Actions tab. Without the app (in tanj) they say they need it.</summary>
public sealed class DesktopActions(IDesktopActions? desktop) : IBatchOperation
{
    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        // The Share sheet takes files only.
        if (inputs.All(File.Exists))
            yield return new Target("share", Strings.Share, Strings.WindowsShareAndNearbySharing, Section.File)
            {
                IsBatch = true,
                Kind = TargetKind.Action,
                Quiet = true,
                Running = Strings.OpeningShare,
                Done = Strings.Shared,
            };
        yield return new Target("float", Strings.Float, Strings.ParkOnTheShelfDragOutLater, Section.File)
        {
            IsBatch = true,
            Kind = TargetKind.Action,
            Quiet = true,
            Running = Strings.Floating,
            Done = Strings.Floated,
        };
    }

    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
    {
        if (desktop is null)
            throw new ConversionException(string.Format(Strings.NeedsTheTanjeloAppSelectTheFiles, target.Label.TrimEnd('…')));
        var paths = inputs.Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')).ToList();
        if (target.Key == "share") await desktop.ShareAsync(paths);
        else await desktop.FloatAsync(paths);
        return [];
    }
}
