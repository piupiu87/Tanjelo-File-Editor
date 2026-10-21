namespace Tanjelo.Core.Converters;

/// <summary>Fallback for Office documents when Microsoft Office (or the matching Office app) isn't installed.</summary>
public sealed class LibreOfficeConverter(Toolbox tools) : IConverter
{
    static Target T(string key, string label, string description) => new(key, label, description, Section.Document);

    static readonly Target Pdf = T("pdf", "PDF", "Exported by LibreOffice");
    static readonly Target[] Writer = [Pdf, T("docx", "DOCX", "Word document"), T("odt", "ODT", "OpenDocument text"), T("rtf", "RTF", "Rich text"), T("txt", "TXT", "Plain text, UTF-8")];
    static readonly Target[] Impress = [Pdf, T("pptx", "PPTX", "PowerPoint presentation"), T("odp", "ODP", "OpenDocument presentation")];
    static readonly Target[] Calc = [Pdf, T("xlsx", "XLSX", "Excel workbook"), T("ods", "ODS", "OpenDocument spreadsheet"), T("csv", "CSV", "First sheet, UTF-8")];

    public IEnumerable<Target> TargetsFor(string input)
    {
        if (tools.LibreOffice is null) return [];
        return Formats.KindOf(input) switch
        {
            FileKind.WordDocument when !tools.Word => Writer,
            FileKind.Presentation when !tools.PowerPoint => Impress,
            FileKind.Spreadsheet when !tools.Excel => Calc,
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
            throw new ConversionException($"LibreOffice couldn't convert this file: {result.LastError}");

        var output = OutputPaths.Beside(input, target.Key);
        File.Move(produced, output);
        return [output];
    }
}
