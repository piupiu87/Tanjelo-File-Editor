namespace Tanjelo.Core.Converters;

/// <summary>Fallback for Office documents when Microsoft Office (or the matching Office app) isn't installed.</summary>
public sealed class LibreOfficeConverter(Toolbox tools) : IConverter
{
    static Target T(string key, string label, string description) => new(key, label, description, Section.Document);

    static readonly Target Pdf = T("pdf", Strings.PDF, Strings.ExportedByLibreOffice);
    static readonly Target[] Writer = [Pdf, T("docx", Strings.DOCX, Strings.WordDocument), T("odt", Strings.ODT, Strings.OpenDocumentText), T("rtf", Strings.RTF, Strings.RichText), T("txt", Strings.TXT, Strings.PlainTextUTF8)];
    static readonly Target[] Impress = [Pdf, T("pptx", Strings.PPTX, Strings.PowerPointPresentation), T("odp", Strings.ODP, Strings.OpenDocumentPresentation)];
    static readonly Target[] Calc = [Pdf, T("xlsx", Strings.XLSX, Strings.ExcelWorkbook), T("ods", Strings.ODS, Strings.OpenDocumentSpreadsheet), T("csv", Strings.CSV, Strings.FirstSheetUTF8)];

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.LibreOffice is null) return [];
        return Formats.KindOf(input) switch
        {
            FileKind.WordDocument when !tools.Word => Writer,
            FileKind.Presentation when !tools.PowerPoint => Impress,
            // TSV import is only wired up for Excel; LibreOffice would need an explicit import filter.
            FileKind.Spreadsheet when !tools.Excel && Formats.Key(input) != "tsv" => Calc,
            _ => [],
        };
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        using var temp = OutputPaths.CreateTemp();
        var filter = target.Key switch
        {
            "txt" => "txt:Text (encoded):UTF8",
            "csv" => "csv:Text - txt - csv (StarCalc):44,34,76",
            _ => target.Key,
        };
        // A private profile lets this run even while the user has LibreOffice open.
        var profile = new Uri(Path.Combine(temp.Path, "profile")).AbsoluteUri;
        var result = await ProcessRunner.RunAsync(tools.LibreOffice!,
            ["--headless", "--norestore", "--nolockcheck", $"-env:UserInstallation={profile}", "--convert-to", filter, "--outdir", temp.Path, Path.GetFullPath(input)],
            ctx.Cancellation);

        var produced = Directory.EnumerateFiles(temp.Path, "*." + target.Key).FirstOrDefault();
        if (result.ExitCode != 0 || produced is null)
            throw new ConversionException(string.Format(Strings.LibreOfficeCouldnTConvertThisFile, result.LastError));

        var output = OutputPaths.Beside(input, target.Key);
        File.Move(produced, output);
        return [output];
    }
}
