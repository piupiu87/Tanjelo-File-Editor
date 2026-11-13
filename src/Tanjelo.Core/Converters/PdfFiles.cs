using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Tanjelo.Core.Converters;

/// <summary>Page-level PDF work with PDFsharp. Pages are copied as they are, never re-rendered.</summary>
static partial class PdfFiles
{
    /// <summary>Opens a PDF to copy pages out of, with errors worded for the user.</summary>
    public static PdfDocument OpenForImport(string path) => Open(path, PdfDocumentOpenMode.Import, null);

    /// <summary>Opens a PDF to save a changed copy of, such as one with new metadata.</summary>
    /// <param name="password">The password to open it with; one that limits changes needs its permissions password.</param>
    public static PdfDocument OpenForChanges(string path, string? password = null) => Open(path, PdfDocumentOpenMode.Modify, password);

    static PdfDocument Open(string path, PdfDocumentOpenMode mode, string? password)
    {
        try
        {
            return password is null ? PdfReader.Open(path, mode) : PdfReader.Open(path, password, mode);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConversionException(password is null ? Strings.ThisPDFLimitsChanges : Strings.ThatPasswordOnlyOpensIt, ex);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConversionException(password is null ? Strings.ThisPDFIsPasswordProtected : Strings.ThatPasswordIsnTRight, ex);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            throw new ConversionException(Strings.ThisDoesnTLookLikeAValid3, ex);
        }
    }

    /// <summary>A new document for imported pages, keeping the source's version so features like transparency survive.</summary>
    public static PdfDocument NewLike(PdfDocument source)
    {
        var document = new PdfDocument();
        document.Version = Math.Max(document.Version, source.Version);
        document.Info.Creator = "Tanjelo";
        return document;
    }

    /// <summary>Saves to <paramref name="output"/>, removing a half-written file if that fails.</summary>
    public static void Save(PdfDocument document, string output)
    {
        try
        {
            document.Save(output);
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
    }

    /// <summary>Whether the PDF carries a digital signature, which any change to it makes invalid.</summary>
    public static bool IsSigned(PdfDocument document)
    {
        var fields = document.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        return fields is not null && fields.Elements.Select(OrganizePdfTool.Resolve).OfType<PdfDictionary>().Any(Signed);

        static bool Signed(PdfDictionary field) =>
            (field.Elements.GetName("/FT") == "/Sig" && field.Elements.GetDictionary("/V") is not null)
            || field.Elements.GetArray("/Kids")?.Elements.Select(OrganizePdfTool.Resolve).OfType<PdfDictionary>().Any(Signed) == true;
    }

    /// <summary>Says on the result card, once per file, that the changed copy's signature won't hold.</summary>
    public static void NoteIfSigned(PdfDocument document, JobContext ctx)
    {
        if (IsSigned(document)) ctx.Note(Strings.TheSignatureIsnTValidInTheCopy);
    }

    /// <summary>
    /// How a PDF is encrypted, read from its trailer and encryption dictionary without parsing the file, so the menu
    /// can choose between Protect and Remove password at once. Null when it isn't encrypted.
    /// </summary>
    public static Encryption? EncryptionOf(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return EncryptionOf(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <param name="Permissions">The /P entry, the permission bits; null when the encryption dictionary couldn't be found cheaply.</param>
    public sealed record Encryption(int? Permissions)
    {
        /// <summary>Whether it limits printing, copying or changes, so only its permissions password removes the protection.</summary>
        public bool? Limits => Permissions is { } bits ? (bits & AllPermissions) != AllPermissions : null;
    }

    const int TailBytes = 64 * 1024, HeadBytes = 16 * 1024, ScanLimit = 64 * 1024 * 1024;

    // The permission bits that matter to people (PDF 2.0, table 22): print, change, copy, comment, fill in forms,
    // assemble and print at full quality. Bit 10, copying for accessibility, is always granted by modern readers.
    const int AllPermissions = 1 << 2 | 1 << 3 | 1 << 4 | 1 << 5 | 1 << 8 | 1 << 10 | 1 << 11;

    [GeneratedRegex(@"/Encrypt\s*(?:(\d+)\s+(\d+)\s+R|<<)")]
    private static partial Regex EncryptEntry();

    [GeneratedRegex(@"/P\s+(-?\d+)")]
    private static partial Regex PermissionsEntry();

    [GeneratedRegex(@"startxref\s+(\d+)")]
    private static partial Regex StartXref();

    static Encryption? EncryptionOf(FileStream file)
    {
        var length = file.Length;
        // The trailer is at the end, or in the cross-reference stream startxref points to (PDF 1.5); a linearized
        // file also has one near the start.
        var tail = Read(file, Math.Max(0, length - TailBytes), TailBytes);
        var places = new List<string> { tail };
        if (StartXref().Matches(tail).LastOrDefault() is { } start
            && long.TryParse(start.Groups[1].ValueSpan, out var offset) && offset >= 0 && offset < length - TailBytes)
            places.Add(Read(file, offset, 8192));
        if (length > TailBytes) places.Add(Read(file, 0, HeadBytes));

        foreach (var text in places)
        {
            if (EncryptEntry().Matches(text).LastOrDefault() is not { } entry) continue;
            if (!entry.Groups[1].Success)
                return new Encryption(PermissionsIn(text[entry.Index..Math.Min(text.Length, entry.Index + 4096)]));
            var header = $"{entry.Groups[1].Value} {entry.Groups[2].Value} obj";
            var dictionary = Find(header, places) ?? Scan(file, header);
            return new Encryption(dictionary is null ? null : PermissionsIn(dictionary));
        }
        return null;
    }

    static int? PermissionsIn(string dictionary)
    {
        var end = dictionary.IndexOf("endobj", StringComparison.Ordinal);
        var match = PermissionsEntry().Match(end < 0 ? dictionary : dictionary[..end]);
        if (!match.Success || !int.TryParse(match.Groups[1].ValueSpan, out var permissions)) return null;
        return permissions;
    }

    /// <summary>The text from an object's header on, if it's in one of these pieces of the file.</summary>
    static string? Find(string header, IEnumerable<string> places)
    {
        foreach (var text in places)
        {
            var at = IndexOfObject(text, header);
            if (at >= 0) return text[at..Math.Min(text.Length, at + 4096)];
        }
        return null;
    }

    /// <summary>Where "12 0 obj" starts, not inside "112 0 obj".</summary>
    static int IndexOfObject(string text, string header)
    {
        for (var at = text.IndexOf(header, StringComparison.Ordinal); at >= 0; at = text.IndexOf(header, at + 1, StringComparison.Ordinal))
            if (at == 0 || !char.IsAsciiDigit(text[at - 1]))
                return at;
        return -1;
    }

    /// <summary>Reads through the file for an object the trailer's neighbourhood doesn't hold; only encrypted files get here.</summary>
    static string? Scan(FileStream file, string header)
    {
        const int Chunk = 1024 * 1024;
        for (long from = 0; from < Math.Min(file.Length, ScanLimit); from += Chunk - 4096)
        {
            var text = Read(file, from, Chunk);
            var at = IndexOfObject(text, header);
            if (at >= 0) return Read(file, from + at, 4096);
        }
        return null;
    }

    static string Read(FileStream file, long from, int count)
    {
        file.Position = from;
        var buffer = new byte[(int)Math.Min(count, file.Length - from)];
        var read = file.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return Encoding.Latin1.GetString(buffer, 0, read);
    }
}
