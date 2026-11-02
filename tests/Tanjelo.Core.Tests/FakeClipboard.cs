using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Stands in for the Windows clipboard: it hands back what a test put on it and records what would have gone on it, so
/// the tests leave the real one alone.
/// </summary>
sealed class FakeClipboard : IClipboard
{
    /// <summary>What <see cref="Read"/> finds, as a test puts it there.</summary>
    public ClipboardData Data { get; set; } = ClipboardData.Empty;

    public uint Sequence { get; set; } = 1;

    public string? Text { get; private set; }

    public IReadOnlyList<string>? Files { get; private set; }

    /// <summary>The file put on as a picture.</summary>
    public string? Picture { get; private set; }

    public ClipboardData Read() => Data;

    public void SetText(string text) => Put(() => Text = text);

    public void SetFiles(IReadOnlyList<string> paths) => Put(() => Files = paths);

    public void SetPicture(string file) => Put(() => Picture = file);

    /// <summary>Whatever was there before is replaced, as on the real clipboard.</summary>
    void Put(Action set)
    {
        (Text, Files, Picture) = (null, null, null);
        set();
        Sequence++;
    }
}
