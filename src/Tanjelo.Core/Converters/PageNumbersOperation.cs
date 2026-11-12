using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Page numbers…: a line of text at the top and one at the bottom of each page, with {page}, {pages}, {date},
/// {file} and {bates} filled in, as real text so it can be searched. Bates numbers carry on across the PDFs in the
/// order they were selected. Each line sits where the page is seen, on turned, cropped and scaled pages too.
/// </summary>
public sealed partial class PageNumbersOperation : IBatchOperation, IPreviewer, IValidator
{
    static readonly Choice[] Places = [new("left", Strings.Left), new("centre", Strings.Centre), new("right", Strings.Right)];

    static Choice Preset(string value) => new(value, Sample(value));

    /// <summary>What a line reads on page 1 of 12, for its button.</summary>
    static string Sample(string line) => Fill(line, 1, 12, "", Strings.Bates, "");

    static readonly Target Entry = new Target("page-numbers", Strings.PageNumbers, Strings.NumbersHeaderAndFooterTextAndBates, Section.Document)
    {
        IsBatch = true,
        Kind = TargetKind.Tool,
        Running = Strings.NumberingPages,
        Done = Strings.PagesNumbered,
        Confirm = Strings.AddToThePages,
    }.WithOptions(
        new Option("top", Strings.TopLine, OptionKind.Text, "") { Optional = true, Presets = Presets(), Hint = Strings.LinesHint },
        new Option("top-place", Strings.TopLineAt, OptionKind.Choice, "centre") { Choices = Places },
        new Option("bottom", Strings.BottomLine, OptionKind.Text, "{page}") { Optional = true, Presets = Presets() },
        new Option("bottom-place", Strings.BottomLineAt, OptionKind.Choice, "centre") { Choices = Places },
        new Option("start", Strings.StartAt, OptionKind.Number, "1") { Min = 0, Max = 999_999, Whole = true, Hint = Strings.StartAtHint },
        new Option("skip", Strings.LeaveOutTheFirst, OptionKind.Number, "0") { Min = 0, Max = 9999, Whole = true, Unit = Strings.PagesUnit, Hint = Strings.LeaveOutHint },
        new Option("size", Strings.FontSize, OptionKind.Number, "10") { Min = 4, Max = 72, Unit = Strings.Pt },
        new Option("colour", Strings.Colour, OptionKind.Colour, "#000000"),
        new Option("margin", Strings.DistanceFromTheEdge, OptionKind.Number, "12") { Min = 0, Max = 100, Unit = Strings.Mm },
        new Option("bates-prefix", Strings.BatesPrefix, OptionKind.Text, "") { Optional = true, ShownWhen = UsesBates, Hint = Strings.BatesPrefixHint },
        new Option("bates-digits", Strings.BatesDigits, OptionKind.Number, "6") { Min = 1, Max = 12, Whole = true, ShownWhen = UsesBates },
        new Option("bates-start", Strings.FirstBatesNumber, OptionKind.Number, "1") { Min = 0, Max = 999_999_999, Whole = true, ShownWhen = UsesBates },
        new Option("bates-suffix", Strings.BatesSuffix, OptionKind.Text, "") { Optional = true, ShownWhen = UsesBates });

    static Condition UsesBates => new("top", "{bates}") { Contains = true, Or = new Condition("bottom", "{bates}") { Contains = true } };

    static Choice[] Presets() => [Preset("{page}"), Preset(Strings.PagePreset), Preset(Strings.PageOfPreset), Preset("{page} / {pages}"), Preset("{bates}")];

    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs) =>
        inputs.Count > 0 && inputs.All(i => Formats.Key(i) == "pdf") ? [Entry] : [];

    /// <summary>The settings, read and checked once.</summary>
    sealed record Settings(string Top, string TopPlace, string Bottom, string BottomPlace, int Start, int Skip, double Size, XColor Colour,
        double Margin, string Prefix, int Digits, long BatesStart, string Suffix)
    {
        public static Settings Of(Target target) => new(
            target.Text("top"), target.Choice("top-place"), target.Text("bottom"), target.Choice("bottom-place"),
            (int)target.Number("start"), (int)target.Number("skip"), target.Number("size"), PdfLettering.ColourOf(target.Colour("colour")),
            target.Number("margin") / 25.4 * 72,
            target.Text("bates-prefix"), (int)target.Number("bates-digits"), (long)target.Number("bates-start"), target.Text("bates-suffix"));

        public bool Bates => Mentions(Top, "{bates}") || Mentions(Bottom, "{bates}");

        public string BatesNumber(long number) => Prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0') + Suffix;

        static bool Mentions(string line, string token) => line.Contains(token, StringComparison.OrdinalIgnoreCase);
    }

    public Task<SettingsProblem?> ValidateAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) =>
        Task.FromResult(target.Text("top").Length == 0 && target.Text("bottom").Length == 0
            ? new SettingsProblem("bottom", Strings.GiveTextForTheTopOrBottomLine)
            : null);

    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var settings = Settings.Of(target);
        if (settings.Top.Length == 0 && settings.Bottom.Length == 0) throw new ConversionException(Strings.GiveTextForTheTopOrBottomLine);
        var outputs = new List<string>();
        var bates = settings.BatesStart;
        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                var name = Path.GetFileName(inputs[i]);
                var prefix = inputs.Count > 1 ? name + ": " : "";
                using var document = Named(name, inputs.Count, () => PdfFiles.OpenForChanges(inputs[i]));
                var first = bates;
                Stamp(document, settings, Formats.BaseName(inputs[i]), Enumerable.Range(0, document.PageCount), Last(settings, document.PageCount), ref bates);
                var output = OutputPaths.Suffixed(inputs[i], "numbered", "pdf");
                PdfFiles.Save(document, output);
                outputs.Add(output);
                if (settings.Bates && bates > first)
                    ctx.Note(string.Format(Strings.BatesRangeOf, name, settings.BatesNumber(first), settings.BatesNumber(bates - 1)));
                if (PdfFiles.IsSigned(document)) ctx.Note(prefix + Strings.TheSignatureIsnTValidInTheCopy);
                ctx.Progress.Report((i + 1.0) / inputs.Count);
            }
        }
        catch
        {
            // Numbers that carry on from file to file make sense only together.
            outputs.ForEach(OutputPaths.TryDelete);
            throw;
        }
        return outputs;
    }, ctx.Cancellation);

    /// <summary>When several PDFs are selected, which of them a problem is with.</summary>
    static T Named<T>(string name, int count, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (ConversionException ex) when (count > 1)
        {
            throw new ConversionException($"{name}: {ex.Message}", ex);
        }
    }

    /// <summary>Adds the lines to the given pages of an open document; <paramref name="bates"/> is the next Bates number.</summary>
    /// <param name="last">The number of the last page, for {pages}.</param>
    static void Stamp(PdfDocument document, Settings settings, string file, IEnumerable<int> pages, int last, ref long bates)
    {
        var date = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);
        foreach (var index in pages)
        {
            if (index < settings.Skip) continue;
            var number = settings.Start + index - settings.Skip;
            var stamper = new PageStamper(document.Pages[index]);
            var (width, height) = (stamper.Geometry.Width, stamper.Geometry.Height);
            var batesNumber = settings.Bates ? settings.BatesNumber(bates++) : "";
            foreach (var (text, place, top) in new[] { (settings.Top, settings.TopPlace, true), (settings.Bottom, settings.BottomPlace, false) })
            {
                if (text.Length == 0) continue;
                var filled = Fill(text, number, last, file, batesNumber, date);
                var line = PdfLettering.Measure(filled, PdfLettering.Font(filled, settings.Size));
                var x = place switch
                {
                    "left" => settings.Margin,
                    "right" => width - settings.Margin - line.Width,
                    _ => (width - line.Width) / 2,
                };
                // The bottom line's lowest point, or the top line's highest, is the margin away from the edge.
                var y = top ? height - settings.Margin - line.Height : settings.Margin;
                stamper.Place(PdfLettering.Form(document, line, settings.Colour), Affine.Translate(x, y));
            }
            stamper.Finish();
        }
    }

    [GeneratedRegex(@"\{(page|pages|date|file|bates)\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    internal static string Fill(string line, int page, int pages, string file, string bates, string date) =>
        Token().Replace(line, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "page" => page.ToString(CultureInfo.CurrentCulture),
            "pages" => pages.ToString(CultureInfo.CurrentCulture),
            "date" => date,
            "file" => file,
            _ => bates,
        });

    /// <summary>The first numbered page of the first PDF with its lines, and where the Bates numbers run.</summary>
    public async Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx)
    {
        var settings = Settings.Of(target);
        var (page, facts) = await Task.Run(() =>
        {
            using var source = PdfFiles.OpenForImport(inputs[0]);
            var index = Math.Min(settings.Skip, source.PageCount - 1);
            var date = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);
            var file = Formats.BaseName(inputs[0]);
            var last = Last(settings, source.PageCount);
            var number = settings.Start + index - settings.Skip;
            var bates = settings.BatesStart;
            var lines = new[] { settings.Top, settings.Bottom }.Where(l => l.Length > 0)
                .Select(l => Fill(l, number, last, file, settings.BatesNumber(bates), date)).ToList();
            var facts = new List<Fact>();
            if (lines.Count > 0 && index >= settings.Skip)
                facts.Add(new Fact(Strings.FirstNumberedPage, string.Join(" · ", lines)));
            if (settings.Bates)
            {
                var total = 0L;
                foreach (var input in inputs)
                {
                    using var pdf = input == inputs[0] ? null : PdfFiles.OpenForImport(input);
                    total += Math.Max(0, (pdf ?? source).PageCount - settings.Skip);
                }
                facts.Add(new Fact(Strings.Bates, string.Format(Strings.BatesRange, settings.BatesNumber(bates), settings.BatesNumber(bates + Math.Max(0, total - 1)))));
            }
            // The page alone, numbered as it will be.
            using var copy = PdfFiles.NewLike(source);
            copy.AddPage(source.Pages[index]);
            if (index >= settings.Skip)
                Stamp(copy, settings with { Skip = 0, Start = number }, file, [0], last, ref bates);
            using var saved = new MemoryStream();
            copy.Save(saved, closeStream: false);
            return (saved.ToArray(), facts);
        }, ctx.Cancellation);
        return new Preview(await PdfConverter.PreviewAsync(page, request.MaxSize), facts);
    }

    /// <summary>The number the last page gets, for {pages}.</summary>
    static int Last(Settings settings, int pageCount) => settings.Start + Math.Max(0, pageCount - settings.Skip) - 1;
}
