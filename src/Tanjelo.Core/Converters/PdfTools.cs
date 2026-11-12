using PdfSharp.Pdf;

namespace Tanjelo.Core.Converters;

/// <summary>Splits a PDF into one file per page. Pages are copied as they are, so nothing is re-rendered or lost.</summary>
public sealed class SplitPdfTool : IConverter
{
    static readonly Target Split = new("split-pdf", Strings.SplitIntoPages, Strings.OnePDFPerPage, Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = Strings.Splitting,
        Done = Strings.SplitIntoPages,
    };

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Split] : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var source = PdfFiles.OpenForImport(input);
        var count = source.PageCount;
        if (count < 2)
            throw new ConversionException(Strings.ThisPDFHasOnlyOnePage);

        var baseName = Formats.BaseName(input);
        var folder = OutputPaths.Unique(OutputPaths.DirectoryOf(input), baseName + " pages", "");
        Directory.CreateDirectory(folder);
        var digits = count.ToString().Length;
        try
        {
            for (var i = 0; i < count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                using var page = PdfFiles.NewLike(source);
                page.AddPage(source.Pages[i]);
                PdfFiles.Save(page, OutputPaths.Unique(folder, $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}", "pdf"));
                ctx.Progress.Report((i + 1.0) / count);
            }
        }
        catch
        {
            OutputPaths.TryDelete(folder);
            throw;
        }
        return [folder];
    }, ctx.Cancellation);
}

/// <summary>
/// Merges PDFs into one, in the order they were selected, with pictures and documents among them made into PDFs by
/// their own converters first (in a scratch folder, so nothing is left beside them). Each file gets a top-level
/// bookmark with its name, and a PDF's own bookmarks go under it.
/// </summary>
public sealed class MergePdfOperation : IBatchOperation
{
    /// <summary>The engine, set by it, for making PDFs of pictures and documents.</summary>
    internal ConverterEngine? Engine { get; set; }

    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        if (inputs.Count < 2) yield break;
        var pdfs = inputs.Count(IsPdf);
        // Pictures alone have One PDF; with a PDF among them, they merge like documents.
        if (pdfs == 0 || (pdfs < inputs.Count && (Engine is null || !inputs.All(i => IsPdf(i) || Engine.CanMakePdf(i))))) yield break;
        yield return new Target("merge-pdf", Strings.MergeIntoOnePDF,
            pdfs == inputs.Count ? Strings.InTheOrderSelectedABookmarkEach : Strings.PDFsPicturesAndDocumentsInTheOrderSelected, Section.Document)
        {
            IsBatch = true,
            Kind = TargetKind.Tool,
            Running = Strings.Merging,
            Done = Strings.MergedIntoOnePDF,
        };
    }

    static bool IsPdf(string path) => Formats.Key(path) == "pdf";

    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
    {
        using var temp = OutputPaths.CreateTemp();
        var progress = ctx.Progress;
        var pdfs = new List<string>();
        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                if (IsPdf(inputs[i]))
                {
                    pdfs.Add(inputs[i]);
                    continue;
                }
                var step = i;
                ctx.Progress = new ActionProgress<double>(f => progress.Report((step + f) / (inputs.Count + 1)));
                pdfs.Add(await Named(inputs[i], () => Engine!.MakePdfAsync(inputs[i], temp.Path, ctx)));
            }
        }
        finally
        {
            ctx.Progress = progress;
        }

        return await Task.Run<IReadOnlyList<string>>(() =>
        {
            PdfDocument? merged = null;
            try
            {
                for (var i = 0; i < inputs.Count; i++)
                {
                    ctx.Cancellation.ThrowIfCancellationRequested();
                    using var source = Named(inputs[i], () => PdfFiles.OpenForImport(pdfs[i]));
                    merged ??= PdfFiles.NewLike(source);
                    merged.Version = Math.Max(merged.Version, source.Version);
                    var copies = new Dictionary<int, PdfPage>();
                    for (var page = 0; page < source.PageCount; page++)
                        copies[page] = merged.AddPage(source.Pages[page]);
                    if (copies.Count > 0)
                    {
                        var bookmark = merged.Outlines.Add(Formats.BaseName(inputs[i]), copies[0], opened: false);
                        OrganizePdfTool.Bookmarks.Write(bookmark.Outlines, OrganizePdfTool.Bookmarks.Read(source), page => copies.GetValueOrDefault(page));
                    }
                    ctx.Progress.Report((i + 1.0) / (inputs.Count + 1));
                }
                var output = OutputPaths.Unique(OutputPaths.DirectoryOf(inputs[0]), "Merged", "pdf");
                PdfFiles.Save(merged!, output);
                return [output];
            }
            finally
            {
                merged?.Dispose();
            }
        }, ctx.Cancellation);
    }

    // The whole selection is one job, so say which file was the problem.
    static T Named<T>(string path, Func<T> work)
    {
        try
        {
            return work();
        }
        catch (ConversionException ex)
        {
            throw new ConversionException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }

    static async Task<T> Named<T>(string path, Func<Task<T>> work)
    {
        try
        {
            return await work();
        }
        catch (ConversionException ex)
        {
            throw new ConversionException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }
}
