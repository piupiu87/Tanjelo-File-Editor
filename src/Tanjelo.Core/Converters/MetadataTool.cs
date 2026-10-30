using ImageMagick;
using PdfSharp.Pdf;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Shows a file's metadata (title, author, camera, location…) and saves a copy with some of it removed or a new
/// title and author. Pictures and media are copied as they are: only the metadata around them changes.
/// </summary>
public sealed class MetadataTool(Toolbox tools) : IConverter, IPreviewer
{
    static readonly Option Title = new("title", Strings.Title, OptionKind.Text, "") { Optional = true, Remember = false, Hint = Strings.NewTitleEmptyKeepsTheCurrentOne };

    static Option Author(string label) =>
        new("author", label, OptionKind.Text, "") { Optional = true, Remember = false, Hint = string.Format(Strings.NewEmptyKeepsTheCurrentOne, label.ToLowerInvariant()) };

    static readonly Option Location = new("location", Strings.RemoveTheLocation, OptionKind.Toggle, "true")
    {
        Hint = Strings.WhereItWasTakenAsPhonesAnd,
    };

    static readonly Option Camera = new("camera", Strings.RemoveCameraDetails, OptionKind.Toggle, "false") { Hint = Strings.MakeModelLensAndSerialNumbers };

    static readonly Option Everything = new("all", Strings.RemoveEverythingElse, OptionKind.Toggle, "false")
    {
        Hint = Strings.DatesSoftwareCommentsAndTheRestAnything,
    };

    static Target Metadata(Section section, params Option[] options) =>
        new Target("metadata", Strings.Metadata, Strings.ViewEditOrRemove, section)
        {
            Kind = TargetKind.Tool,
            Running = Strings.SavingMetadata,
            Done = Strings.SavedWithNewMetadata,
            Confirm = Strings.SaveCopy,
        }.WithOptions(options);

    static readonly Target ImageTarget = Metadata(Section.Image, Title, Author(Strings.Author), Location, Camera, Everything);
    static readonly Target VideoTarget = Metadata(Section.Video, Title, Author(Strings.Artist), Location, Everything);
    static readonly Target AudioTarget = Metadata(Section.Audio, Title, Author(Strings.Artist), Everything);
    static readonly Target PdfTarget = Metadata(Section.Document, Title, Author(Strings.Author), Everything);

    public IEnumerable<Target> TargetsFor(string input)
    {
        var key = Formats.Key(input);
        if (ImageMetadata.Formats.Contains(key)) return [ImageTarget];
        if (key == "pdf") return [PdfTarget];
        if (tools.Ffmpeg is null || tools.Ffprobe is null) return [];
        if (Formats.Videos.Contains(key)) return [VideoTarget];
        if (Formats.Audio.Contains(key)) return [AudioTarget];
        return [];
    }

    static MetadataEdit Edit(Target target)
    {
        bool Flag(string name) => target.Options.Any(o => o.Name == name) && target.Flag(name);
        string? Text(string name) => target.Text(name).Trim() is { Length: > 0 } text ? text : null;
        return new MetadataEdit(Text("title"), Text("author"), Flag("location"), Flag("camera"), Flag("all"));
    }

    static string NothingToDo => Strings.ThereSNothingToChangeNoneOf;

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        var edit = Edit(target);
        var output = OutputPaths.Suffixed(input, "edited");
        var key = Formats.Key(input);
        if (ImageMetadata.Formats.Contains(key) || key == "pdf")
        {
            await Task.Run(() =>
            {
                try
                {
                    if (key == "pdf") SavePdf(input, output, edit);
                    else SaveImage(input, output, edit);
                }
                catch
                {
                    OutputPaths.TryDelete(output);
                    throw;
                }
            }, ctx.Cancellation);
        }
        else
        {
            var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
            if (edit is { Title: null, Author: null, All: false } && !(edit.Location && LocationTags.Any(media.Tags.ContainsKey)))
                throw new ConversionException(NothingToDo);
            await Ffmpeg.RunFirstWorkingAsync(tools, input, output, MediaPlans(key, edit), media.Duration, ctx);
        }
        ctx.Progress.Report(1);
        return [output];
    }

    static void SaveImage(string input, string output, MetadataEdit edit)
    {
        var metadata = ImageMetadata.Read(input);
        if (!metadata.Apply(edit)) throw new ConversionException(NothingToDo);
        metadata.Write(output);
    }

    static void SavePdf(string input, string output, MetadataEdit edit)
    {
        using var document = PdfFiles.OpenForChanges(input);
        if (!ApplyPdf(document, edit)) throw new ConversionException(NothingToDo);
        PdfFiles.Save(document, output);
    }

    /// <summary>
    /// The document information gets the new values; an XMP packet, which readers prefer, would contradict them, so
    /// it goes. PDFsharp names itself as the producer whenever it saves, and Tanjelo as the creator once it's cleared.
    /// </summary>
    static bool ApplyPdf(PdfDocument document, MetadataEdit edit)
    {
        var info = document.Info;
        var removes = edit.All && (info.Elements.Count > 0 || document.Internals.Catalog.Elements.ContainsKey("/Metadata"));
        if (edit is { Title: null, Author: null } && !removes) return false;
        if (edit.All)
        {
            foreach (var name in info.Elements.Keys.ToList())
                info.Elements.Remove(name);
            info.Creator = "Tanjelo";
        }
        if (edit.All || edit.Title is not null || edit.Author is not null)
            document.Internals.Catalog.Elements.Remove("/Metadata");
        if (edit.Title is { } title) info.Title = title;
        if (edit.Author is { } author) info.Author = author;
        return true;
    }

    /// <summary>Tags in which phones and cameras record where a video was made.</summary>
    internal static readonly string[] LocationTags = ["location", "location-eng", "com.apple.quicktime.location.ISO6709", "com.android.capture.location"];

    /// <summary>A stream copy with changed tags; if some odd stream (a timecode, say) won't copy, just picture, sound and subtitles.</summary>
    static IEnumerable<string[]> MediaPlans(string key, MetadataEdit edit)
    {
        var tags = new List<string> { "-map_metadata", edit.All ? "-1" : "0" };
        if (edit.All) tags.AddRange(["-map_metadata:s", "-1", "-fflags", "+bitexact"]); // bitexact: ffmpeg doesn't sign the file either
        if (edit.Location) tags.AddRange(LocationTags.SelectMany(t => new[] { "-metadata", t + "=" }));
        if (edit.Title is { } title) tags.AddRange(["-metadata", "title=" + title]);
        if (edit.Author is { } author) tags.AddRange(["-metadata", "artist=" + author]);
        if (key == "mp3") tags.AddRange(["-id3v2_version", "3"]); // ID3v2.3 is what older Windows and car stereos read
        yield return ["-map", "0", "-c", "copy", .. tags];
        yield return ["-map", "0:v?", "-map", "0:a?", "-map", "0:s?", "-c", "copy", .. tags];
    }

    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var input = inputs[0];
        var edit = Edit(target);
        var key = Formats.Key(input);
        if (ImageMetadata.Formats.Contains(key))
        {
            return await Task.Run(() =>
            {
                var before = ImageMetadata.Read(input);
                var after = ImageMetadata.Read(input);
                after.Apply(edit);
                var facts = Compare(Fields(before), Fields(after));
                using var picture = new MagickImage();
                picture.Read(input);
                picture.AutoOrient();
                picture.Thumbnail((uint)request.MaxSize, (uint)request.MaxSize);
                return new Preview(picture.ToByteArray(MagickFormat.Png), facts);
            }, ctx.Cancellation);
        }
        if (key == "pdf")
        {
            return await Task.Run(() =>
            {
                // Saved to memory and read back, so the preview shows what PDFsharp really writes.
                using var document = PdfFiles.OpenForChanges(input);
                var before = PdfFields(document);
                if (!ApplyPdf(document, edit)) return new Preview(null, Compare(before, before));
                using var saved = new MemoryStream();
                document.Save(saved, closeStream: false);
                using var after = PdfSharp.Pdf.IO.PdfReader.Open(saved, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                return new Preview(null, Compare(before, PdfFields(after)));
            }, ctx.Cancellation);
        }

        var media = await Ffmpeg.ProbeAsync(tools, input, ctx.Cancellation);
        var facts = Compare(MediaFields(media.Tags, key), MediaFields(Simulate(media.Tags, key, edit), key));
        var frame = media.Video is null ? null : await Ffmpeg.FrameAsync(tools, input, media.Duration / 3, request.MaxSize, ctx.Cancellation);
        return new Preview(frame, facts);
    }

    static List<(string Label, string? Value)> Fields(ImageMetadata m) =>
    [
        (Strings.Title, m.Title),
        (Strings.Author, m.Author),
        (Strings.Camera, m.Camera),
        (Strings.Location, m.Location),
        (Strings.Taken, m.Taken),
        (Strings.Software, m.Software),
        (Strings.Comment, m.Comment),
        (Strings.Other, m.OtherExif > 0 ? string.Format(Strings.MoreEXIFFields, m.OtherExif) : null),
        (Strings.XMP, m.Xmp is null ? null : "present"),
    ];

    static List<(string Label, string? Value)> PdfFields(PdfDocument document)
    {
        string? Get(string name) => document.Info.Elements.GetString(name) is { Length: > 0 } value ? value : null;
        return
        [
            (Strings.Title, Get("/Title")),
            (Strings.Author, Get("/Author")),
            (Strings.Subject, Get("/Subject")),
            (Strings.Keywords, Get("/Keywords")),
            (Strings.MadeWith, Get("/Creator")),
            (Strings.Producer, Get("/Producer")),
            (Strings.Created, Get("/CreationDate") is { } created ? PdfDate(created) : null),
            // No line for XMP: PDFsharp writes a fresh XMP copy of the information above whenever it saves.
        ];
    }

    /// <summary>Video locations are written as in ISO 6709, "+52.3676+004.9041/"; anything else is shown as it is.</summary>
    static string Iso6709(string text)
    {
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)");
        return match.Success
            && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var latitude)
            && double.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var longitude)
                ? Describe.Place(latitude, longitude)
                : text;
    }

    /// <summary>"D:20240701140300+02'00'" becomes "2024-07-01 14:03".</summary>
    static string PdfDate(string text) =>
        text.StartsWith("D:", StringComparison.Ordinal) && text.Length >= 14
            ? $"{text[2..6]}-{text[6..8]}-{text[8..10]} {text[10..12]}:{text[12..14]}"
            : text;

    /// <summary>The tags a copy will have: ffmpeg's MP4 and MOV writer only keeps the standard ones, so phones' own (com.…) go.</summary>
    static IReadOnlyDictionary<string, string> Simulate(IReadOnlyDictionary<string, string> tags, string key, MetadataEdit edit)
    {
        var after = new Dictionary<string, string>(tags, StringComparer.OrdinalIgnoreCase);
        if (edit.All) after.Clear();
        if (edit.Location) foreach (var tag in LocationTags) after.Remove(tag);
        if (key is "mp4" or "m4v" or "mov" or "m4a")
            foreach (var tag in after.Keys.Where(k => k.StartsWith("com.", StringComparison.Ordinal)).ToList())
                after.Remove(tag);
        if (edit.Title is { } title) after["title"] = title;
        if (edit.Author is { } author) after["artist"] = author;
        return after;
    }

    static readonly string[] Known = ["title", "artist", "album", "date", "creation_time", "comment", "encoder", "com.apple.quicktime.make", "com.apple.quicktime.model"];

    static List<(string Label, string? Value)> MediaFields(IReadOnlyDictionary<string, string> tags, string key)
    {
        string? Get(string name) => tags.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
        var others = tags.Keys.Count(k => !Known.Contains(k, StringComparer.OrdinalIgnoreCase) && !LocationTags.Contains(k, StringComparer.OrdinalIgnoreCase)
            && k is not ("major_brand" or "minor_version" or "compatible_brands"));
        return
        [
            (Strings.Title, Get("title")),
            (Strings.Artist, Get("artist")),
            (Strings.Album, Get("album")),
            (Strings.Date, Get("date") ?? Get("creation_time")),
            (Strings.Location, LocationTags.Select(Get).FirstOrDefault(v => v is not null) is { } place ? Iso6709(place) : null),
            (Strings.Camera, string.Join(" ", new[] { Get("com.apple.quicktime.make"), Get("com.apple.quicktime.model") }.OfType<string>()) is { Length: > 0 } c ? c : null),
            (Strings.Comment, Get("comment")),
            (Strings.MadeWith, Get("encoder")),
            (Strings.Other, others > 0 ? string.Format(Strings.MoreTags, others) : null),
        ];
    }

    /// <summary>Lines such as "Location: 52.37° N, 4.9° E → removed" and "Title: Holiday → Beach day".</summary>
    internal static IReadOnlyList<Fact> Compare(List<(string Label, string? Value)> before, List<(string Label, string? Value)> after)
    {
        var facts = new List<Fact>();
        foreach (var (label, old) in before)
        {
            var now = after.FirstOrDefault(a => a.Label == label).Value;
            if (old is null && now is null) continue;
            facts.Add(new Fact(label, (old, now) switch
            {
                (null, _) => string.Format(Strings.New, now),
                (_, null) => string.Format(Strings.Removed, old),
                _ when old == now => old,
                _ => $"{old} → {now}",
            }));
        }
        return facts.Count > 0 ? facts : [new Fact(Strings.Metadata2, Strings.NoneFound)];
    }
}
