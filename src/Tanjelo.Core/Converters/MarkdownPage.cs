using System.Net;
using System.Text;
using ImageMagick;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Tanjelo.Core.Converters;

/// <summary>Markdown as a styled web page or as plain text, with Markdig: tables, task lists, footnotes and the like.</summary>
internal static class MarkdownPage
{
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseFootnotes()
        .UseAutoLinks()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseYamlFrontMatter()
        .UseAutoIdentifiers()
        .Build();

    /// <summary>Light, print-friendly and readable on screen: a centred column that fills the page when printed.</summary>
    public const string Style = """
        :root { color-scheme: light; }
        body { margin: 0; background: #fff; color: #1f2328; font: 16px/1.6 "Segoe UI", system-ui, sans-serif; }
        main { max-width: 46em; margin: 0 auto; padding: 2.5em 1.5em; }
        h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.5em 0 .5em; font-weight: 600; }
        h1 { font-size: 2em; }
        h2 { font-size: 1.5em; padding-bottom: .2em; border-bottom: 1px solid #d8dee4; }
        h3 { font-size: 1.25em; }
        main > :first-child { margin-top: 0; }
        p, ul, ol, dl, blockquote, pre, table, figure { margin: 0 0 1em; }
        li > p { margin: 0 0 .4em; }
        a { color: #0b5cad; }
        code, pre, kbd { font-family: "Cascadia Mono", Consolas, monospace; font-size: .9em; }
        code { padding: .1em .3em; border-radius: 4px; background: #f2f4f7; }
        pre { padding: .8em 1em; border-radius: 6px; background: #f6f8fa; white-space: pre-wrap; overflow-wrap: anywhere; }
        pre code { padding: 0; background: none; }
        blockquote { margin-left: 0; padding: 0 1em; color: #57606a; border-left: 4px solid #d0d7de; }
        table { border-collapse: collapse; }
        th, td { padding: .35em .7em; border: 1px solid #d0d7de; }
        th { background: #f6f8fa; }
        img { max-width: 100%; height: auto; }
        hr { margin: 2em 0; border: 0; border-top: 1px solid #d8dee4; }
        .contains-task-list { padding-left: 1.2em; list-style: none; }
        .task-list-item input { margin: 0 .4em 0 0; }
        .footnotes { font-size: .9em; color: #57606a; }
        @media print {
          body { font-size: 11pt; }
          main { max-width: none; padding: 0; }
          a { color: inherit; }
          pre, blockquote, tr, img { break-inside: avoid; }
          h1, h2, h3, h4 { break-after: avoid; }
          .footnote-back-ref { display: none; }
        }
        """;

    public static MarkdownDocument Parse(string markdown) => Markdown.Parse(markdown, Pipeline);

    /// <summary>
    /// A self-contained page: the stylesheet is inline and pictures from the computer are embedded. For printing,
    /// pictures from the web are replaced by their alt text and the page may only load what it holds.
    /// </summary>
    /// <param name="source">The Markdown file, which relative picture paths start from.</param>
    public static string Html(string markdown, string source, bool forPrint)
    {
        var document = Parse(markdown);
        var folder = OutputPaths.DirectoryOf(source);
        foreach (var image in document.Descendants<LinkInline>().Where(l => l.IsImage).ToList())
            Embed(image, folder, forPrint);

        var body = new StringWriter();
        var renderer = new HtmlRenderer(body);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        return Page(Title(document, source), body.ToString(), forPrint ? folder : null);
    }

    /// <param name="printFolder">For printing: the folder raw HTML's relative links start from.</param>
    internal static string Page(string title, string body, string? printFolder)
    {
        var head = printFolder is null ? "" : $"""
            {PrintPolicy}
            <base href="{WebUtility.HtmlEncode(new Uri(printFolder.TrimEnd('\\') + "\\").AbsoluteUri)}">

            """;
        return $"""
            <!DOCTYPE html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            {head}<title>{WebUtility.HtmlEncode(title)}</title>
            <style>
            {Style}
            </style>
            </head>
            <body>
            <main>
            {body}</main>
            </body>
            </html>

            """;
    }

    /// <summary>What a page printed offline may load: only what it holds, and files on this computer.</summary>
    internal const string PrintPolicy = """<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: file:; style-src 'unsafe-inline'; font-src data: file:">""";

    /// <summary>The title in the YAML front matter, if there is one.</summary>
    internal static string? FrontMatterTitle(MarkdownDocument document)
    {
        if (document.Descendants<YamlFrontMatterBlock>().FirstOrDefault() is { } front)
            foreach (var line in front.Lines.ToString().Split('\n'))
                if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase) && line[6..].Trim().Trim('"', '\'') is { Length: > 0 } title)
                    return title;
        return null;
    }

    /// <summary>The front matter's title, else the first heading, else the file's name.</summary>
    internal static string Title(MarkdownDocument document, string source)
    {
        if (FrontMatterTitle(document) is { } title)
            return title;
        if (document.Descendants<HeadingBlock>().FirstOrDefault() is { } heading && Inlines(heading.Inline) is { Length: > 0 } text)
            return text;
        return Formats.BaseName(source);
    }

    static void Embed(LinkInline image, string folder, bool forPrint)
    {
        var url = image.Url ?? "";
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        if (LocalPath(url, folder) is { } path && File.Exists(path) && DataUri(path) is { } data)
        {
            image.Url = data;
            return;
        }
        if (!forPrint) return; // a web page may show pictures from the web; a printout made offline can't

        var alt = Inlines(image);
        if (alt.Length == 0)
        {
            image.Remove();
            return;
        }
        var italic = new EmphasisInline { DelimiterChar = '*', DelimiterCount = 1 };
        italic.AppendChild(new LiteralInline(alt));
        image.ReplaceBy(italic, copyChildren: false);
    }

    /// <summary>The file a picture's address points to on this computer, or null for one on the web.</summary>
    static string? LocalPath(string url, string folder)
    {
        if (url.Length == 0) return null;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return uri.IsFile ? uri.LocalPath : null;
            var relative = Uri.UnescapeDataString(url.Split('#', '?')[0]).Replace('/', '\\');
            return Path.GetFullPath(Path.Combine(folder, relative));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }

    static string? DataUri(string path)
    {
        var mime = Formats.Key(path) switch
        {
            "png" => "image/png",
            "jpg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "avif" => "image/avif",
            "svg" => "image/svg+xml",
            "bmp" => "image/bmp",
            "ico" => "image/x-icon",
            _ => null,
        };
        byte[] bytes;
        if (mime is not null)
        {
            bytes = File.ReadAllBytes(path);
        }
        else if (Formats.KindOf(path) == FileKind.Image)
        {
            // HEIC, TIFF, PSD and others that browsers can't show go in as PNG.
            try
            {
                using var frames = ImageConverter.Read(path);
                var image = frames[0];
                image.AutoOrient();
                (bytes, mime) = (image.ToByteArray(MagickFormat.Png), "image/png");
            }
            catch (ConversionException)
            {
                return null;
            }
        }
        else
        {
            return null;
        }
        return $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
    }

    // ---- Plain text

    /// <summary>
    /// The text without markup: paragraphs on one line each, lists with • or numbers, code and quotes indented,
    /// table cells separated by tabs, a link's address in brackets after its text, and footnotes at the end.
    /// </summary>
    public static string PlainText(string markdown)
    {
        var document = Parse(markdown);
        var text = Blocks(document);
        var notes = document.Descendants<FootnoteGroup>().SelectMany(g => g.OfType<Footnote>())
            .Select(note => $"[{note.Order}] {Blocks(note)}").ToList();
        if (notes.Count > 0) text += "\r\n\r\n" + string.Join("\r\n", notes);
        return text + "\r\n";
    }

    static string Blocks(ContainerBlock container) =>
        string.Join("\r\n\r\n", container.Select(Block).Where(t => !string.IsNullOrEmpty(t)));

    static string? Block(Block block) => block switch
    {
        // First: front matter is a kind of code block to Markdig.
        YamlFrontMatterBlock or LinkReferenceDefinitionGroup or HtmlBlock or FootnoteGroup => null,
        HeadingBlock heading => Inlines(heading.Inline),
        ParagraphBlock paragraph => Inlines(paragraph.Inline),
        ListBlock list => List(list),
        QuoteBlock quote => Indent(Blocks(quote), "    "),
        CodeBlock code => Indent(code.Lines.ToString().ReplaceLineEndings("\r\n").TrimEnd(), "    "),
        ThematicBreakBlock => "---",
        Table table => string.Join("\r\n", table.OfType<TableRow>()
            .Select(row => string.Join("\t", row.OfType<TableCell>().Select(cell => Blocks(cell).ReplaceLineEndings(" "))))),
        ContainerBlock container => Blocks(container),
        _ => null,
    };

    static string List(ListBlock list)
    {
        var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;
        return string.Join("\r\n", list.OfType<ListItemBlock>().Select(item =>
        {
            var marker = list.IsOrdered ? $"{number++}. " : "• ";
            // Items stay on consecutive lines, even in a list with blank lines between its items.
            var body = Blocks(item).Replace("\r\n\r\n", "\r\n");
            return marker + Indent(body, new string(' ', marker.Length)).TrimStart();
        }));
    }

    static string Indent(string text, string prefix) =>
        string.Join("\r\n", text.Split("\r\n").Select(line => line.Length == 0 ? line : prefix + line));

    internal static string Inlines(ContainerInline? container)
    {
        if (container is null) return "";
        var text = new StringBuilder();
        foreach (var inline in container)
            Append(text, inline);
        return text.ToString().Trim();
    }

    static void Append(StringBuilder text, Inline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                text.Append(literal.Content.ToString());
                break;
            case CodeInline code:
                text.Append(code.Content);
                break;
            case LineBreakInline lineBreak:
                text.Append(lineBreak.IsHard ? "\r\n" : " ");
                break;
            case HtmlEntityInline entity:
                text.Append(entity.Transcoded.ToString());
                break;
            case TaskList task:
                text.Append(task.Checked ? "[x]" : "[ ]"); // the text after it starts with its own space
                break;
            case FootnoteLink { IsBackLink: false } note:
                text.Append('[').Append(note.Footnote.Order).Append(']');
                break;
            case AutolinkInline link:
                text.Append(link.Url);
                break;
            case LinkInline { IsImage: true } image:
                text.Append(Inlines(image));
                break;
            case LinkInline link:
                var label = Inlines(link);
                text.Append(label);
                if (link.Url is { Length: > 0 } url && !url.StartsWith('#') && url != label && !link.IsAutoLink)
                    text.Append(" (").Append(url).Append(')');
                break;
            case ContainerInline container:
                foreach (var child in container)
                    Append(text, child);
                break;
        }
    }
}
