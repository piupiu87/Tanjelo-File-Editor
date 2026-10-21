using PdfSharp.Pdf;

namespace Tanjelo.Core.Converters;

/// <summary>Splits a PDF into one file per page. Pages are copied as they are, so nothing is re-rendered or lost.</summary>
public sealed class SplitPdfTool : IConverter
{
    static readonly Target Split = new("split-pdf", "Split into pages", "One PDF per page", Section.Document)
    {
        Kind = TargetKind.Tool,
        Running = "Splitting…",
        Done = "Split into pages",
    };

    public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "pdf" ? [Split] : [];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        using var source = PdfFiles.OpenForImport(input);
        var count = source.PageCount;
        if (count < 2)
            throw new ConversionException("This PDF has only one page.");

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

/// <summary>Merges several PDFs into one, in the order they were selected.</summary>
public sealed class MergePdfOperation : IBatchOperation
{
    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        if (inputs.Count >= 2 && inputs.All(i => Formats.Key(i) == "pdf"))
            yield return new Target("merge-pdf", "Merge into one PDF", "In the order selected", Section.Document)
            {
                IsBatch = true,
                Kind = TargetKind.Tool,
                Running = "Merging…",
                Done = "Merged into one PDF",
            };
    }

    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        PdfDocument? merged = null;
        try
        {
            for (var i = 0; i < inputs.Count; i++)
            {
                ctx.Cancellation.ThrowIfCancellationRequested();
                using var source = Open(inputs[i]);
                merged ??= PdfFiles.NewLike(source);
                merged.Version = Math.Max(merged.Version, source.Version);
                foreach (var page in source.Pages)
                    merged.AddPage(page);
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

    // The whole selection is one job, so say which file was the problem.
    static PdfDocument Open(string path)
    {
        try
        {
            return PdfFiles.OpenForImport(path);
        }
        catch (ConversionException ex)
        {
            throw new ConversionException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }
}
