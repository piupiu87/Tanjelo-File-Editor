using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Tanjelo.Core.Converters;

/// <summary>SRT and WebVTT subtitles, converted in-process. Bold, italic and underline survive; other styling is dropped.</summary>
public sealed partial class SubtitleConverter : IConverter
{
    static readonly Target[] Outputs =
    [
        new("srt", Strings.SRT, Strings.SubRipPlaysAnywhere, Section.Document),
        new("vtt", Strings.VTT, Strings.WebVTTForWebVideo, Section.Document),
        new("txt", Strings.TXT, Strings.JustTheWords, Section.Document),
    ];

    public IEnumerable<Target> TargetsFor(string input) => Formats.KindOf(input) == FileKind.Subtitle ? Outputs : [];

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var cues = Parse(TextFiles.Read(input), isVtt: Formats.Key(input) == "vtt");
        if (cues.Count == 0)
            throw new ConversionException(Strings.NoSubtitlesWereFoundInThisFile);

        var (text, encoding) = target.Key switch
        {
            // A byte order mark helps older players and TVs recognise UTF-8 subtitles; WebVTT is always UTF-8.
            "srt" => (ToSrt(cues), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)),
            "vtt" => (ToVtt(cues), new UTF8Encoding(false)),
            "txt" => (ToText(cues), new UTF8Encoding(false)),
            _ => throw new ConversionException(string.Format(Strings.CanTWriteSubtitles, target.Label)),
        };
        var output = OutputPaths.Beside(input, target.Key);
        await File.WriteAllTextAsync(output, text, encoding, ctx.Cancellation);
        return [output];
    }

    /// <summary>One subtitle. Lines hold WebVTT-style markup: text escaped as &amp;amp; and &amp;lt;, plus &lt;b&gt;, &lt;i&gt; and &lt;u&gt;.</summary>
    internal sealed record Cue(TimeSpan Start, TimeSpan End, IReadOnlyList<string> Lines);

    internal static List<Cue> Parse(string text, bool isVtt)
    {
        var cues = new List<Cue>();
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var block = new List<string>();
        foreach (var line in lines.Append(""))
        {
            if (line.Trim().Length > 0)
            {
                block.Add(line);
                continue;
            }
            if (block.Count > 0 && ParseBlock(block, isVtt) is { } cue)
                cues.Add(cue);
            block.Clear();
        }
        return cues;
    }

    // An SRT block is "index, timing, text..."; a WebVTT cue is "[id], timing, text...". Blocks without a timing
    // line (the WEBVTT header, NOTE, STYLE and REGION blocks) are skipped.
    static Cue? ParseBlock(List<string> block, bool isVtt)
    {
        var timing = block.FindIndex(l => l.Contains("-->", StringComparison.Ordinal));
        if (timing < 0 || timing > 1) return null;
        var match = TimingLine().Match(block[timing]);
        if (!match.Success || !TryParseTime(match.Groups[1].Value, out var start) || !TryParseTime(match.Groups[2].Value, out var end))
            return null;
        var lines = block.Skip(timing + 1).Select(l => Normalize(l.Trim(), isVtt)).Where(l => l.Length > 0).ToList();
        return new Cue(start, end, lines);
    }

    /// <summary>Reads "01:02:03,456", "01:02:03.456" and WebVTT's hour-less "02:03.456".</summary>
    internal static bool TryParseTime(string text, out TimeSpan time)
    {
        time = default;
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3) return false;
        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours)) return false;
        if (!int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)) return false;
        if (!double.TryParse(parts[^1].Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)) return false;
        time = new TimeSpan(hours, minutes, 0) + TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        return true;
    }

    /// <summary>Keeps bold, italic and underline tags and escapes the text; drops other tags and SSA overrides like {\an8}.</summary>
    static string Normalize(string line, bool isVtt)
    {
        var result = new StringBuilder();
        foreach (var part in Tags().Split(SsaOverride().Replace(line, "")))
        {
            if (part.StartsWith('<') && part.EndsWith('>'))
            {
                // WebVTT allows classes on tags ("<b.loud>"); SRT writes them in any case ("<I>").
                var tag = BasicTag().Match(part);
                if (tag.Success) result.Append('<').Append(tag.Groups[1].Value).Append(tag.Groups[2].Value.ToLowerInvariant()).Append('>');
            }
            else
            {
                var plain = isVtt ? WebUtility.HtmlDecode(part) : part;
                result.Append(plain.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;"));
            }
        }
        return result.ToString().Trim();
    }

    internal static string ToSrt(IReadOnlyList<Cue> cues)
    {
        var text = new StringBuilder();
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            text.Append(i + 1).Append("\r\n");
            text.Append(Time(cue.Start, ',')).Append(" --> ").Append(Time(cue.End, ',')).Append("\r\n");
            foreach (var line in cue.Lines)
                text.Append(WebUtility.HtmlDecode(line)).Append("\r\n");
            text.Append("\r\n");
        }
        return text.ToString();
    }

    internal static string ToVtt(IReadOnlyList<Cue> cues)
    {
        var text = new StringBuilder("WEBVTT\r\n\r\n");
        foreach (var cue in cues)
        {
            text.Append(Time(cue.Start, '.')).Append(" --> ").Append(Time(cue.End, '.')).Append("\r\n");
            foreach (var line in cue.Lines)
                text.Append(line).Append("\r\n");
            text.Append("\r\n");
        }
        return text.ToString();
    }

    /// <summary>The words alone, one subtitle line per line. Lines repeated by rolling captions appear once.</summary>
    internal static string ToText(IReadOnlyList<Cue> cues)
    {
        var lines = new List<string>();
        foreach (var line in cues.SelectMany(c => c.Lines))
        {
            var plain = WebUtility.HtmlDecode(Tags().Replace(line, "")).Trim();
            if (plain.Length > 0 && (lines.Count == 0 || lines[^1] != plain))
                lines.Add(plain);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    static string Time(TimeSpan time, char separator) =>
        $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}{separator}{time.Milliseconds:000}";

    [GeneratedRegex(@"^\s*(\S+)\s+-->\s+(\S+)")]
    private static partial Regex TimingLine();

    // A tag starts right after "<" (so "Fish < chips" is text): <i>, </font>, <c.yellow>, <00:00:01.500>.
    [GeneratedRegex("(</?[A-Za-z0-9][^<>]*>)")]
    private static partial Regex Tags();

    [GeneratedRegex(@"^<(/?)([biuBIU])(?:\.[^>]*)?>$")]
    private static partial Regex BasicTag();

    [GeneratedRegex(@"\{\\[^}]*\}")]
    private static partial Regex SsaOverride();
}

static class TextFiles
{
    /// <summary>
    /// Reads a text file in whatever encoding it uses: a byte order mark wins, then UTF-8 if the bytes are valid
    /// UTF-8, and otherwise the Windows ANSI code page, which is what older subtitle files tend to be in.
    /// </summary>
    public static string Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using (var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, throwOnInvalidBytes: true), detectEncodingFromByteOrderMarks: true))
        {
            try { return reader.ReadToEnd(); }
            catch (DecoderFallbackException) { }
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
    }
}
