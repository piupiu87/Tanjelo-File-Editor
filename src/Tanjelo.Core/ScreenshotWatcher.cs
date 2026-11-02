namespace Tanjelo.Core;

/// <summary>What's known about one change to the clipboard without reading what was copied.</summary>
/// <param name="Owner">The program that copied it, such as "SnippingTool.exe"; null when no window owns the clipboard, as after Print Screen.</param>
/// <param name="Formats">The formats on the clipboard, by name: "CF_BITMAP", "CF_DIB", "PNG", "ExcludeClipboardContentFromMonitorProcessing"…</param>
/// <param name="Sequence">The clipboard's number for this copy.</param>
public sealed record ClipboardChange(string? Owner, IReadOnlyCollection<string> Formats, uint Sequence, DateTime At)
{
    /// <summary>Tanjelo copied it itself, such as a result.</summary>
    public bool FromTanjelo { get; init; }

    /// <summary>False when CanIncludeInClipboardHistory says 0, as password managers mark what they copy.</summary>
    public bool HistoryAllowed { get; init; } = true;
}

/// <summary>
/// Decides which changes to the clipboard are screenshots to park on the shelf, when to read them, and whether one takes
/// the place of the one before. Only the owner and the formats are looked at; the picture is read only once a
/// screenshot has been left alone for a moment. It's told what happened and when, so it runs without Windows' clipboard.
/// </summary>
public sealed class ScreenshotWatcher
{
    /// <summary>
    /// The programs that take screenshots: Snipping Tool, the Win+Shift+S overlay, and Windows 10's Snip &amp; Sketch.
    /// Others, such as ShareX or Greenshot, would go here.
    /// </summary>
    public static readonly IReadOnlySet<string> ScreenshotApps =
        new HashSet<string>(["SnippingTool.exe", "ScreenClippingHost.exe", "ScreenSketch.exe"], StringComparer.OrdinalIgnoreCase);

    /// <summary>How long the clipboard stays unchanged before a screenshot is read: tools put a picture there in several steps.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(1);

    /// <summary>A screenshot copied again by the same app this soon after, at the same size, is the same one edited.</summary>
    public static readonly TimeSpan ReplaceWithin = TimeSpan.FromSeconds(30);

    static readonly HashSet<string> Pictures = ["PNG", "CF_DIB", "CF_DIBV5", "CF_BITMAP"];
    static readonly HashSet<string> Bitmaps = ["CF_BITMAP", "CF_DIB", "CF_DIBV5", "CF_PALETTE"];

    ClipboardChange? _pending;
    DateTime _lastChange;
    (string? Owner, string File, int Width, int Height, DateTime At)? _parked;

    /// <summary>The setting "Park screenshots on the shelf"; off, nothing is a screenshot.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether a change from this owner could be a screenshot, and so whether its formats are worth listing at all.</summary>
    public static bool MightBeScreenshot(string? owner) => owner is null || ScreenshotApps.Contains(owner);

    /// <summary>
    /// Takes in a change to the clipboard, which replaces any screenshot still waiting. Returns whether it's a
    /// screenshot, to be read once the clipboard is quiet; otherwise <paramref name="skipped"/> says why when it was a
    /// picture, for the log.
    /// </summary>
    public bool Notice(ClipboardChange change, out string? skipped)
    {
        _lastChange = change.At;
        skipped = null;
        var screenshot = Enabled && IsScreenshot(change, out skipped);
        _pending = screenshot ? change : null;
        return screenshot;
    }

    static bool IsScreenshot(ClipboardChange change, out string? skipped)
    {
        skipped = null;
        // Tanjelo's own copies, and what password managers and the like ask clipboard watchers not to look at.
        if (change.FromTanjelo || !change.HistoryAllowed || change.Formats.Contains("ExcludeClipboardContentFromMonitorProcessing"))
            return false;
        var picture = change.Formats.Any(Pictures.Contains);
        if (change.Owner is { } owner ? ScreenshotApps.Contains(owner) && picture
            : change.Formats.Count > 0 && change.Formats.All(Bitmaps.Contains)) // Print Screen without Snipping Tool
            return true;
        if (picture) skipped = $"a picture from {change.Owner ?? "no app"} isn't taken for a screenshot";
        return false;
    }

    /// <summary>The screenshot to read now, once the clipboard has been quiet for <see cref="QuietTime"/>; null when there's none yet.</summary>
    public ClipboardChange? Due(DateTime now)
    {
        if (_pending is not { } pending || now - _lastChange < QuietTime) return null;
        _pending = null;
        return pending;
    }

    /// <summary>
    /// A screenshot was saved as <paramref name="file"/>: returns the one it takes the place of on the shelf, or null
    /// when it's a new item. Snipping Tool copies its picture again after each edit, so one from the same app within
    /// <see cref="ReplaceWithin"/>, at the same size, replaces the last one parked while that's still unused.
    /// </summary>
    /// <param name="unused">Whether a file is still on the shelf, and hasn't been dragged, copied, edited or converted.</param>
    public string? Replaces(ClipboardChange change, string file, int width, int height, DateTime at, Func<string, bool> unused)
    {
        string? replaced = null;
        if (_parked is { } last && !string.Equals(last.File, file, StringComparison.OrdinalIgnoreCase)
            && string.Equals(last.Owner, change.Owner, StringComparison.OrdinalIgnoreCase)
            && at - last.At <= ReplaceWithin && (last.Width, last.Height) == (width, height) && unused(last.File))
            replaced = last.File;
        _parked = (change.Owner, file, width, height, at);
        return replaced;
    }
}
