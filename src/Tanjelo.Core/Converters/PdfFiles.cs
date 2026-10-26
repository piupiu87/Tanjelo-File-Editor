using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Tanjelo.Core.Converters;

/// <summary>Page-level PDF work with PDFsharp. Pages are copied as they are, never re-rendered.</summary>
static class PdfFiles
{
    /// <summary>Opens a PDF to copy pages out of, with errors worded for the user.</summary>
    public static PdfDocument OpenForImport(string path) => Open(path, PdfDocumentOpenMode.Import);

    /// <summary>Opens a PDF to save a changed copy of, such as one with new metadata.</summary>
    public static PdfDocument OpenForChanges(string path) => Open(path, PdfDocumentOpenMode.Modify);

    static PdfDocument Open(string path, PdfDocumentOpenMode mode)
    {
        try
        {
            return PdfReader.Open(path, mode);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new ConversionException("This PDF is password-protected.", ex);
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            throw new ConversionException("This doesn't look like a valid PDF.", ex);
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
}
