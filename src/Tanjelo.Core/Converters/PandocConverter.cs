using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;

namespace Tanjelo.Core.Converters;

/// <summary>
/// Word, EPUB and Markdown documents through pandoc, which the user installs. pandoc is GPL-licensed, so Tanjelo only
/// ever runs it as a separate program. Markdown becomes DOCX or EPUB; Word documents and EPUBs become Markdown with
/// their pictures in a folder beside it; EPUBs also become DOCX, TXT, one web page or a PDF; Word documents, text,
/// web pages and PDFs (their text and headings) become EPUBs.
/// </summary>
public sealed class PandocConverter(Toolbox tools) : IConverter
{
    public const string NoPandoc = "Word and EPUB conversions need pandoc: winget install JohnMacFarlane.Pandoc";

    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    static Target T(string key, string label, string description) => new(key, label, description, Section.Document);

    static readonly Target Docx = T("docx", "DOCX", "Word document, via pandoc");
    static readonly Target Epub = T("epub", "EPUB", "E-book, via pandoc");
    static readonly Target Markdown = T("md", "Markdown", "Pictures in a folder beside it");
    static readonly Target Txt = T("txt", "TXT", "The text, via pandoc");
    static readonly Target Html = T("html", "HTML", "One web page, pictures included");
    static readonly Target Pdf = T("pdf", "PDF", "Printed offline by Windows' WebView2");
    static readonly Target PdfEpub = T("epub", "EPUB", "E-book of the text and headings only");

    // Plain Markdown pictures: no sizes, no figure wrappers, and no chapter divs or anchor spans from EPUBs.
    const string PlainMarkdownFilter = """
        function Image(img)
          img.attributes.width = nil
          img.attributes.height = nil
          img.attributes.style = nil
          return img
        end
        function Figure(fig) return fig.content end
        function Div(div) return div.content end
        function Span(span) return span.content end
        function RawInline(raw)
          if raw.format == "html" and raw.text:match("^</?label") then return {} end
        end
        """;

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.Pandoc is null) return [];
        return Formats.KindOf(input) switch
        {
            FileKind.Markdown => [Docx, Epub],
            FileKind.WordDocument => Formats.Key(input) switch
            {
                "txt" => [Epub],
                "doc" when !tools.Word => [], // pandoc reads RTF itself, but not the old binary format
                _ => [Markdown, Epub],
            },
            FileKind.Ebook => tools.WebView2 is null ? [Docx, Markdown, Txt, Html] : [Docx, Markdown, Txt, Html, Pdf],
            FileKind.WebPage => [Epub],
            FileKind.Pdf => [PdfEpub],
            _ => [],
        };
    }

    public string? HintFor(string input) =>
        tools.Pandoc is null && Formats.KindOf(input) is FileKind.Markdown or FileKind.Ebook ? NoPandoc : null;

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        input = Path.GetFullPath(input);
        using var temp = OutputPaths.CreateTemp();
        var source = await SourceAsync(input, temp.Path, ctx);
        IReadOnlyList<string> outputs = target.Key switch
        {
            "md" => [await ToMarkdownAsync(input, source, temp.Path, ctx)],
            "pdf" => [await ToPdfAsync(input, source, temp.Path, ctx)],
            _ => [await ConvertAsync(input, source, target.Key, temp.Path, ctx)],
        };
        ctx.Progress.Report(1);
        return outputs;
    }

    /// <summary>A file pandoc can read, the reader to read it with, and the title to give an e-book that has none.</summary>
    sealed record Source(string Path, string Format, string? Title);

    async Task<Source> SourceAsync(string input, string temp, JobContext ctx)
    {
        var name = Formats.BaseName(input);
        switch (Formats.Key(input))
        {
            case "doc" or "rtf" when tools.Word:
                // pandoc can't read DOC, and Word reads RTF better than pandoc does.
                var docx = Path.Combine(temp, name + ".docx");
                await OfficeConverter.SaveAsDocxAsync(input, docx, ctx);
                return new Source(docx, "docx", DocxTitle(docx) ? null : name);
            case "rtf":
                return new Source(input, "rtf", name);
            case "docx" or "docm" or "dotx":
                return new Source(input, "docx", DocxTitle(input) ? null : name);
            case "odt":
                return new Source(input, "odt", OdtTitle(input) ? null : name);
            case "txt":
                var page = Path.Combine(temp, name + ".html");
                await File.WriteAllTextAsync(page, TextAsHtml(TextFiles.Read(input), name), new UTF8Encoding(false), ctx.Cancellation);
                return new Source(page, "html", null);
            case "md":
                var document = MarkdownPage.Parse(TextFiles.Read(input));
                return new Source(input, "commonmark_x", MarkdownPage.FrontMatterTitle(document) is null ? MarkdownPage.Title(document, input) : null);
            case "html":
                return new Source(input, "html", Regex.IsMatch(TextFiles.Read(input), @"<title[^>]*>\s*[^<\s]", RegexOptions.IgnoreCase) ? null : name);
            case "epub":
                return new Source(input, "epub", null);
            case "pdf":
                var text = await PdfText.ReadAsync(input, ctx, new ActionProgress<double>(f => ctx.Progress.Report(0.6 * f)));
                text.EnsureText();
                text.AddNotes(ctx);
                var markdown = Path.Combine(temp, name + ".md");
                await File.WriteAllTextAsync(markdown, text.ToMarkdown(pageBreaks: false), new UTF8Encoding(false), ctx.Cancellation);
                return new Source(markdown, "commonmark_x", text.FirstHeading ?? name);
            default:
                throw new ConversionException("pandoc can't read this file.");
        }
    }

    async Task<string> ConvertAsync(string input, Source source, string key, string temp, JobContext ctx)
    {
        var produced = Path.Combine(temp, "out." + key);
        List<string> args = [source.Path, "-f", source.Format, "-o", produced];
        if (key == "epub" && source.Title is { } title) args.AddRange(["-M", "title=" + title]);
        if (key == "html") args.AddRange(["-t", "html5", "--standalone", "--embed-resources", "--css=" + await StyleSheetAsync(temp, ctx)]);
        if (key == "txt") args.AddRange(["-t", "plain", "--wrap=none"]);
        await RunAsync(args, input, ctx);

        var output = OutputPaths.Beside(input, key);
        File.Move(produced, output);
        return output;
    }

    /// <summary>Markdown with the pictures in "name media" beside it, named after the Markdown file it goes with.</summary>
    async Task<string> ToMarkdownAsync(string input, Source source, string temp, JobContext ctx)
    {
        var output = OutputPaths.Beside(input, "md");
        var directory = OutputPaths.DirectoryOf(output);
        var media = OutputPaths.Unique(directory, Formats.BaseName(output) + " media", "");
        var mediaName = Path.GetFileName(media);
        var filter = Path.Combine(temp, "plain-markdown.lua");
        await File.WriteAllTextAsync(filter, PlainMarkdownFilter, ctx.Cancellation);

        // pandoc runs in the temp folder, so the pictures land there first and the links come out relative.
        var produced = Path.Combine(temp, "out.md");
        await RunAsync([source.Path, "-f", source.Format, "-t", "gfm", "--wrap=none", "--lua-filter=" + filter, "--extract-media=" + mediaName, "-o", produced],
            input, ctx, workingDirectory: temp);

        var markdown = await File.ReadAllTextAsync(produced, ctx.Cancellation);
        var extracted = Path.Combine(temp, mediaName);
        if (Directory.Exists(extracted))
        {
            markdown = Flatten(extracted, mediaName, markdown);
            if (string.Equals(Path.GetPathRoot(extracted), Path.GetPathRoot(media), StringComparison.OrdinalIgnoreCase))
                Directory.Move(extracted, media);
            else
                MoveToFolderAction.MoveAcrossDrives(extracted, media, ctx.Cancellation);
        }
        try
        {
            await File.WriteAllTextAsync(output, markdown, new UTF8Encoding(false), ctx.Cancellation);
        }
        catch
        {
            OutputPaths.TryDelete(media);
            throw;
        }
        return output;
    }

    /// <summary>
    /// pandoc keeps the paths pictures had inside the document, so a DOCX's go in "name media/media". When the
    /// folder holds nothing but one such folder, its pictures move up a level and the links follow.
    /// </summary>
    internal static string Flatten(string folder, string folderName, string markdown)
    {
        if (Directory.EnumerateFiles(folder).Any() || Directory.GetDirectories(folder) is not [var inner]) return markdown;
        var innerName = Path.GetFileName(inner);
        foreach (var entry in Directory.EnumerateFileSystemEntries(inner))
        {
            var destination = Path.Combine(folder, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination);
        }
        Directory.Delete(inner);
        foreach (var name in new[] { folderName, folderName.Replace(" ", "%20") })
            markdown = markdown.Replace($"{name}/{innerName}/", $"{name}/");
        return markdown;
    }

    /// <summary>An EPUB as one web page, printed by WebView2 like Markdown is.</summary>
    async Task<string> ToPdfAsync(string input, Source source, string temp, JobContext ctx)
    {
        var page = Path.Combine(temp, "book.html");
        await RunAsync([source.Path, "-f", source.Format, "-t", "html5", "--standalone", "--embed-resources", "--css=" + await StyleSheetAsync(temp, ctx), "-o", page], input, ctx);
        ctx.Progress.Report(0.5);
        var html = await File.ReadAllTextAsync(page, ctx.Cancellation);
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        if (head >= 0) html = html.Insert(head + "<head>".Length, "\n" + MarkdownPage.PrintPolicy);
        var output = OutputPaths.Beside(input, "pdf");
        await MarkdownConverter.PrintAsync(html, output, ctx);
        return output;
    }

    /// <summary>The stylesheet Markdown pages use, so a book's web page and PDF look the same; pandoc puts it inline.</summary>
    static async Task<string> StyleSheetAsync(string temp, JobContext ctx)
    {
        var css = Path.Combine(temp, "tanjelo.css");
        // pandoc's page has no <main>, so the column is set on the body.
        await File.WriteAllTextAsync(css, MarkdownPage.Style + "\nbody { max-width: 46em; padding: 2.5em 1.5em; margin: 0 auto; }\n@media print { body { max-width: none; padding: 0; } }\n",
            ctx.Cancellation);
        return css;
    }

    async Task RunAsync(List<string> args, string input, JobContext ctx, string? workingDirectory = null)
    {
        // Relative pictures are found next to the input, wherever pandoc runs.
        var folder = OutputPaths.DirectoryOf(input);
        args.Add("--resource-path=" + folder);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Cancellation);
        timeout.CancelAfter(Timeout);
        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(tools.Pandoc!, args, timeout.Token, workingDirectory: workingDirectory ?? folder);
        }
        catch (OperationCanceledException) when (!ctx.Cancellation.IsCancellationRequested)
        {
            throw new ConversionException("pandoc took more than 5 minutes, so it was stopped.");
        }
        if (result.ExitCode != 0)
        {
            var reason = result.LastError;
            throw new ConversionException("pandoc couldn't convert this file: " + (reason.StartsWith("pandoc: ", StringComparison.Ordinal) ? reason[8..] : reason));
        }
    }

    /// <summary>Plain text as a web page pandoc can read: paragraphs where the text has blank lines, and nothing read as markup.</summary>
    internal static string TextAsHtml(string text, string title)
    {
        var paragraphs = Regex.Split(text.ReplaceLineEndings("\n").Trim(), @"\n[ \t]*\n")
            .Select(p => string.Join(" ", p.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)))
            .Where(p => p.Length > 0)
            .Select(p => $"<p>{WebUtility.HtmlEncode(p)}</p>");
        return $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8"><title>{WebUtility.HtmlEncode(title)}</title></head>
            <body>
            {string.Join("\n", paragraphs)}
            </body>
            </html>

            """;
    }

    static bool DocxTitle(string path)
    {
        try
        {
            using var document = WordprocessingDocument.Open(path, false);
            return !string.IsNullOrWhiteSpace(document.PackageProperties.Title);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false; // pandoc will say what's wrong with the file
        }
    }

    static bool OdtTitle(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            if (zip.GetEntry("meta.xml") is not { } meta) return false;
            using var reader = new StreamReader(meta.Open());
            return Regex.IsMatch(reader.ReadToEnd(), @"<dc:title>\s*[^<\s]");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }
}
