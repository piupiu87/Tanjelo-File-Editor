namespace Tanjelo.Core.Converters;

/// <summary>What only the app can do with a selection, because it needs windows: the Share sheet.</summary>
public interface IDesktopActions
{
    /// <summary>Opens the Windows Share sheet for these files, near the pointer.</summary>
    Task ShareAsync(IReadOnlyList<string> files);
}

/// <summary>Share… on the Actions tab. Without the app (in tanj) it says it needs it.</summary>
public sealed class DesktopActions(IDesktopActions? desktop) : IBatchOperation
{
    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        // The Share sheet takes files only.
        if (inputs.All(File.Exists))
            yield return new Target("share", "Share…", "Windows Share and Nearby Sharing", Section.File)
            {
                IsBatch = true,
                Kind = TargetKind.Action,
                Quiet = true,
                Running = "Opening Share…",
                Done = "Shared",
            };
    }

    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
    {
        if (desktop is null)
            throw new ConversionException($"{target.Label.TrimEnd('…')} needs the Tanjelo app: select the files in File Explorer and press its shortcut.");
        await desktop.ShareAsync(inputs.Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')).ToList());
        return [];
    }
}
