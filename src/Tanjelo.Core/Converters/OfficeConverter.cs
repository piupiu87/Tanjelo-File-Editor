using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Tanjelo.Core.Converters;

/// <summary>Word, PowerPoint and Excel files through the installed Microsoft Office (COM automation).</summary>
/// <remarks>
/// Office can block on modal prompts that its alert settings don't cover, so every call runs under a watchdog
/// that shuts down the Office app we started if it stops responding. Files are opened from a temp copy:
/// when Office is killed mid-open it blacklists that path ("it caused a serious error, open anyway?"), and
/// that must never happen to the user's real file.
/// </remarks>
public sealed class OfficeConverter(Toolbox tools) : IConverter
{
    static readonly Target Pdf = new("pdf", "PDF", "Exported by Microsoft Office", Section.Document);

    static readonly Target[] WordOutputs =
    [
        Pdf,
        new("docx", "DOCX", "Word document", Section.Document),
        new("doc", "DOC", "Word 97-2003", Section.Document),
        new("rtf", "RTF", "Rich text", Section.Document),
        new("odt", "ODT", "OpenDocument text", Section.Document),
        new("txt", "TXT", "Plain text, UTF-8", Section.Document),
        new("html", "HTML", "Web page; pictures go in a folder beside it", Section.Document),
        new("png", "PNG", "One image per page", Section.Image),
        new("jpg", "JPG", "One image per page", Section.Image),
    ];

    static readonly Target[] PowerPointOutputs =
    [
        Pdf,
        new("pptx", "PPTX", "PowerPoint presentation", Section.Document),
        new("ppt", "PPT", "PowerPoint 97-2003", Section.Document),
        new("odp", "ODP", "OpenDocument presentation", Section.Document),
        new("png", "PNG", "One image per slide", Section.Image),
        new("jpg", "JPG", "One image per slide", Section.Image),
    ];

    static readonly Target[] ExcelOutputs =
    [
        Pdf,
        new("xlsx", "XLSX", "Excel workbook", Section.Document),
        new("xls", "XLS", "Excel 97-2003", Section.Document),
        new("ods", "ODS", "OpenDocument spreadsheet", Section.Document),
        new("csv", "CSV", "Active sheet, UTF-8", Section.Document),
        new("tsv", "TSV", "Active sheet, tab-separated, UTF-8", Section.Document),
    ];

    public IEnumerable<Target> TargetsFor(string input)
    {
        var key = Formats.Key(input);
        return Formats.KindOf(input) switch
        {
            FileKind.WordDocument when tools.Word => key == "txt" ? WordOutputs.Where(t => t.Key is "pdf" or "docx" or "html" or "png" or "jpg") : WordOutputs,
            FileKind.Pdf when tools.Word => [new Target("docx", "DOCX", "Editable Word document", Section.Document)],
            FileKind.Presentation when tools.PowerPoint => PowerPointOutputs,
            // Plain-text tables offer the formats worth turning them into, not every Excel format.
            FileKind.Spreadsheet when tools.Excel => key is "csv" or "tsv" ? ExcelOutputs.Where(t => t.Key is "xlsx" or "pdf" or "csv" or "tsv") : ExcelOutputs,
            _ => [],
        };
    }

    public string? HintFor(string input) =>
        Formats.KindOf(input) is FileKind.WordDocument or FileKind.Presentation or FileKind.Spreadsheet
        && !tools.Word && !tools.PowerPoint && !tools.Excel && tools.LibreOffice is null
            ? "Office documents need Microsoft Office or LibreOffice."
            : Formats.Key(input) == "tsv" && !tools.Excel ? "TSV files need Microsoft Excel." : null;

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        ctx.Cancellation.ThrowIfCancellationRequested();
        var office = ctx.Session(() => new OfficeSession());
        var original = Path.GetFullPath(input);

        using var temp = OutputPaths.CreateTemp();
        var copy = Path.Combine(temp.Path, Path.GetFileName(original));
        File.Copy(original, copy);

        var kind = Formats.KindOf(input);
        if (kind == FileKind.WordDocument && target.Key is "png" or "jpg")
        {
            // Word can't save pages as images, so it exports a PDF and Windows renders the pages from that.
            var pdf = Path.Combine(temp.Path, "pages.pdf");
            await Guarded(office, ctx, () => [office.ConvertWord(copy, pdf, "pdf")]);
            ctx.Progress.Report(0.5);
            return await PdfConverter.RenderAsync(pdf, original, target.Key, ctx, new ActionProgress<double>(f => ctx.Progress.Report(0.5 + f / 2)));
        }

        using var stopPromptWatcher = new CancellationTokenSource();
        var promptWatcher = kind == FileKind.Pdf ? PdfImportPrompt.ConfirmUntilAsync(stopPromptWatcher.Token) : Task.CompletedTask;
        try
        {
            return await Guarded(office, ctx, () => kind switch
            {
                FileKind.WordDocument or FileKind.Pdf => [office.ConvertWord(copy, OutputPaths.Beside(original, target.Key), target.Key)],
                FileKind.Presentation => office.ConvertPowerPoint(copy, original, target.Key),
                FileKind.Spreadsheet => [office.ConvertExcel(copy, original, target.Key)],
                _ => throw new ConversionException("Not an Office document."),
            });
        }
        finally
        {
            stopPromptWatcher.Cancel();
            await promptWatcher;
        }
    }

    /// <summary>
    /// Excel quotes every cell that contains a comma, even in tab-separated text, and most TSV readers would keep
    /// those quotes. This re-quotes only the cells that need it: ones holding a tab, a line break or a quote.
    /// </summary>
    internal static string Requote(string text)
    {
        var result = new StringBuilder(text.Length);
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : '\n';
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else if (i < text.Length) field.Append(c);
                else { quoted = false; i--; } // unterminated quote: end the field here
                continue;
            }
            if (c == '"' && field.Length == 0) { quoted = true; continue; }
            if (c is '\t' or '\n' or '\r')
            {
                var value = field.ToString();
                result.Append(value.IndexOfAny(['\t', '\n', '\r', '"']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value);
                field.Clear();
                if (i < text.Length) result.Append(c);
                continue;
            }
            field.Append(c);
        }
        return result.ToString();
    }

    static async Task<IReadOnlyList<string>> Guarded(OfficeSession office, JobContext ctx, Func<IReadOnlyList<string>> work)
    {
        var started = DateTime.Now;
        var task = office.Worker.InvokeAsync(work);
        var deadline = Task.Delay(ctx.Options.OfficeTimeout, ctx.Cancellation).ContinueWith(_ => { }, TaskScheduler.Default);
        if (await Task.WhenAny(task, deadline) == task)
            return await task;

        // The COM call is stuck (usually on a hidden prompt). Killing the app makes it return with an error.
        var killed = office.Abort(started);
        if (killed)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(15)); } catch { /* expected: the call fails once the app is gone */ }
        }
        ctx.Cancellation.ThrowIfCancellationRequested();
        throw new ConversionException(killed
            ? "Office stopped responding, so it was closed. The file may be damaged or need a password."
            : "Office stopped responding. Check for a dialog in your open Office window.");
    }

    /// <summary>
    /// Keeps each Office app running for the whole batch. Apps we started are quit at the end; an instance the
    /// user already had open (visible) is borrowed and left running, with any settings we touched restored.
    /// </summary>
    sealed class OfficeSession : IDisposable
    {
        // A wrong password makes Office fail fast on protected files instead of showing a password prompt.
        const string NoPassword = "\u0001tanjelo";
        const int MsoAutomationSecurityForceDisable = 3;

        public StaWorker Worker { get; } = new("Office automation");

        readonly List<int> _ownedProcesses = [];
        volatile bool _aborted;
        dynamic? _word, _powerPoint, _excel;
        bool _ownsWord, _ownsPowerPoint, _ownsExcel;
        int _wordAlerts;

        /// <summary>Opens <paramref name="source"/> in Word and writes it to <paramref name="output"/> in the <paramref name="key"/> format.</summary>
        public string ConvertWord(string source, string output, string key)
        {
            var word = Word();
            dynamic doc = Formats.Key(source) == "pdf"
                // A document password makes Word's PDF import treat the file as encrypted, so leave it out here.
                ? word.Documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: false)
                : word.Documents.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                    PasswordDocument: NoPassword, Visible: false, NoEncodingDialog: true);
            try
            {
                if (key == "pdf")
                {
                    doc.ExportAsFixedFormat(OutputFileName: output, ExportFormat: 17 /* wdExportFormatPDF */, OpenAfterExport: false,
                        CreateBookmarks: 1 /* wdExportCreateHeadingBookmarks */);
                }
                else
                {
                    int format = key switch
                    {
                        "docx" => 16, // wdFormatDocumentDefault
                        "doc" => 0,   // wdFormatDocument97
                        "rtf" => 6,   // wdFormatRTF
                        "odt" => 23,  // wdFormatOpenDocumentText
                        "txt" => 2,   // wdFormatText
                        "html" => 10, // wdFormatFilteredHTML: clean HTML without Office-only markup
                        _ => throw new ConversionException($"Word can't save as {key}."),
                    };
                    if (key is "txt" or "html")
                        doc.SaveAs2(FileName: output, FileFormat: format, AddToRecentFiles: false, Encoding: 65001);
                    else
                        doc.SaveAs2(FileName: output, FileFormat: format, AddToRecentFiles: false);
                }
            }
            finally
            {
                doc.Close(SaveChanges: 0);
                Marshal.FinalReleaseComObject(doc);
            }
            return output;
        }

        public IReadOnlyList<string> ConvertPowerPoint(string source, string original, string key)
        {
            var app = PowerPoint();
            dynamic pres = app.Presentations.Open(FileName: source, ReadOnly: -1 /* msoTrue */, Untitled: 0, WithWindow: 0);
            try
            {
                if (key is "png" or "jpg")
                    return ExportSlides(pres, original, key);

                var output = OutputPaths.Beside(original, key);
                int format = key switch
                {
                    "pdf" => 32,  // ppSaveAsPDF
                    "pptx" => 24, // ppSaveAsOpenXMLPresentation
                    "ppt" => 1,   // ppSaveAsPresentation
                    "odp" => 35,  // ppSaveAsOpenDocumentPresentation
                    _ => throw new ConversionException($"PowerPoint can't save as {key}."),
                };
                pres.SaveAs(output, format);
                return [output];
            }
            finally
            {
                pres.Close();
                Marshal.FinalReleaseComObject(pres);
            }
        }

        static IReadOnlyList<string> ExportSlides(dynamic pres, string original, string key)
        {
            int count = pres.Slides.Count;
            var baseName = Formats.BaseName(original);
            var directory = OutputPaths.DirectoryOf(original);
            if (count > 1)
                directory = OutputPaths.Unique(directory, baseName + " slides", "");
            Directory.CreateDirectory(directory);

            const int width = 1920;
            double slideWidth = pres.PageSetup.SlideWidth, slideHeight = pres.PageSetup.SlideHeight;
            var height = (int)Math.Round(width * slideHeight / slideWidth);
            var digits = count.ToString().Length;
            var outputs = new List<string>();
            for (var i = 1; i <= count; i++)
            {
                var name = count == 1 ? baseName : $"{baseName}-{i.ToString().PadLeft(digits, '0')}";
                var output = OutputPaths.Unique(directory, name, key);
                pres.Slides.Item(i).Export(output, key == "png" ? "PNG" : "JPG", width, height);
                outputs.Add(output);
            }
            return count > 1 ? [directory] : outputs;
        }

        public string ConvertExcel(string source, string original, string key)
        {
            var excel = Excel();
            var output = OutputPaths.Beside(original, key);
            dynamic book = Formats.Key(source) == "tsv"
                ? OpenTabSeparated(excel, source)
                : excel.Workbooks.Open(Filename: source, UpdateLinks: 0, ReadOnly: true, Password: NoPassword, AddToMru: false);
            string? unicodeText = null;
            try
            {
                if (key == "pdf")
                {
                    book.ExportAsFixedFormat(Type: 0 /* xlTypePDF */, Filename: output, OpenAfterPublish: false);
                }
                else if (key == "tsv")
                {
                    // Excel only writes tab-separated text as UTF-16; it's re-encoded once the workbook is closed.
                    unicodeText = Path.Combine(Path.GetDirectoryName(source)!, Guid.NewGuid().ToString("N") + ".txt");
                    book.SaveAs(Filename: unicodeText, FileFormat: 42 /* xlUnicodeText */);
                }
                else
                {
                    int format = key switch
                    {
                        "xlsx" => 51, // xlOpenXMLWorkbook
                        "xls" => 56,  // xlExcel8
                        "ods" => 60,  // xlOpenDocumentSpreadsheet
                        "csv" => 62,  // xlCSVUTF8
                        _ => throw new ConversionException($"Excel can't save as {key}."),
                    };
                    book.SaveAs(Filename: output, FileFormat: format);
                }
            }
            finally
            {
                book.Close(SaveChanges: false);
                Marshal.FinalReleaseComObject(book);
            }
            if (unicodeText is not null)
            {
                // With a byte order mark, like Excel's own UTF-8 CSV, so Excel reads it back correctly.
                var table = Requote(File.ReadAllText(unicodeText, Encoding.Unicode));
                File.WriteAllText(output, table, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            }
            return output;
        }

        /// <summary>Opens a .tsv as tab-separated text; Excel's own guess would read it as one column.</summary>
        static dynamic OpenTabSeparated(dynamic excel, string source)
        {
            excel.Workbooks.OpenText(Filename: source, Origin: TextOrigin(source), StartRow: 1, DataType: 1 /* xlDelimited */,
                TextQualifier: 1 /* xlTextQualifierDoubleQuote */, ConsecutiveDelimiter: false,
                Tab: true, Semicolon: false, Comma: false, Space: false, Other: false);
            // OpenText doesn't return the workbook. It activates it, and it's named after our temp copy.
            dynamic book = excel.ActiveWorkbook;
            if (book is null || !string.Equals((string)book.Name, Path.GetFileName(source), StringComparison.OrdinalIgnoreCase))
                throw new ConversionException("Excel couldn't open this file.");
            return book;
        }

        /// <summary>The code page Excel should read a text file in: UTF-16 or UTF-8 when it is, otherwise the ANSI code page.</summary>
        static int TextOrigin(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes is [0xFF, 0xFE, ..]) return 1200;
            try
            {
                new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
                return 65001;
            }
            catch (DecoderFallbackException)
            {
                return 2; // xlWindows
            }
        }

        /// <summary>
        /// Called from the watchdog (not the STA thread): kills the Office apps we started, plus any PDF import
        /// helper launched during the stuck call. Returns false when the stuck app is the user's own instance.
        /// </summary>
        public bool Abort(DateTime callStarted)
        {
            _aborted = true;
            int[] owned;
            lock (_ownedProcesses) owned = [.. _ownedProcesses];
            foreach (var id in owned)
                Kill(() => Process.GetProcessById(id));
            foreach (var reflow in Process.GetProcessesByName("PDFREFLOW"))
                if (StartTime(reflow) >= callStarted.AddSeconds(-2)) Kill(() => reflow);
                else reflow.Dispose();
            return owned.Length > 0;
        }

        // After an abort the COM references point at dead processes; start fresh on the next file.
        void ResetIfAborted()
        {
            if (!_aborted) return;
            foreach (var app in new[] { _word, _powerPoint, _excel })
                if (app is not null) Try(() => Marshal.FinalReleaseComObject(app));
            _word = _powerPoint = _excel = null;
            lock (_ownedProcesses) _ownedProcesses.Clear();
            _aborted = false;
        }

        dynamic Word()
        {
            ResetIfAborted();
            if (_word is not null) return _word;
            _word = Create("Word.Application", "WINWORD");
            _ownsWord = !(bool)_word.Visible;
            _wordAlerts = _word.DisplayAlerts;
            _word.DisplayAlerts = 0; // wdAlertsNone
            if (_ownsWord) _word.AutomationSecurity = MsoAutomationSecurityForceDisable;
            return _word;
        }

        dynamic PowerPoint()
        {
            ResetIfAborted();
            if (_powerPoint is not null) return _powerPoint;
            _powerPoint = Create("PowerPoint.Application", "POWERPNT");
            _ownsPowerPoint = (int)_powerPoint.Visible == 0 && (int)_powerPoint.Presentations.Count == 0;
            if (_ownsPowerPoint)
            {
                _powerPoint.DisplayAlerts = 1; // ppAlertsNone
                _powerPoint.AutomationSecurity = MsoAutomationSecurityForceDisable;
            }
            return _powerPoint;
        }

        dynamic Excel()
        {
            ResetIfAborted();
            if (_excel is not null) return _excel;
            _excel = Create("Excel.Application", "EXCEL");
            _ownsExcel = !(bool)_excel.Visible;
            if (_ownsExcel)
            {
                _excel.DisplayAlerts = false;
                _excel.ScreenUpdating = false;
                _excel.AutomationSecurity = MsoAutomationSecurityForceDisable;
            }
            return _excel;
        }

        /// <summary>Starts (or attaches to) an Office app and remembers its process if we were the ones who started it.</summary>
        dynamic Create(string progId, string processName)
        {
            var type = Type.GetTypeFromProgID(progId) ?? throw new ConversionException($"{progId.Split('.')[0]} isn't installed.");
            var before = ProcessIds(processName);
            dynamic app;
            try
            {
                app = Activator.CreateInstance(type)!;
            }
            catch (COMException ex)
            {
                throw new ConversionException($"Couldn't start {progId.Split('.')[0]}: {ex.Message}", ex);
            }
            lock (_ownedProcesses)
                _ownedProcesses.AddRange(ProcessIds(processName).Except(before));
            return app;
        }

        static HashSet<int> ProcessIds(string name)
        {
            var processes = Process.GetProcessesByName(name);
            var ids = processes.Select(p => p.Id).ToHashSet();
            foreach (var p in processes) p.Dispose();
            return ids;
        }

        static DateTime StartTime(Process process)
        {
            try { return process.StartTime; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return DateTime.MinValue; }
        }

        static void Kill(Func<Process> get)
        {
            try
            {
                using var process = get();
                process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            Worker.InvokeAsync(() =>
            {
                ResetIfAborted();
                if (_word is not null)
                {
                    if (_ownsWord) Try(() => _word.Quit(SaveChanges: 0));
                    else Try(() => _word.DisplayAlerts = _wordAlerts);
                    Marshal.FinalReleaseComObject(_word);
                }
                if (_powerPoint is not null)
                {
                    if (_ownsPowerPoint) Try(() => _powerPoint.Quit());
                    Marshal.FinalReleaseComObject(_powerPoint);
                }
                if (_excel is not null)
                {
                    if (_ownsExcel) Try(() => _excel.Quit());
                    Marshal.FinalReleaseComObject(_excel);
                }
                _word = _powerPoint = _excel = null;
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }).Wait(TimeSpan.FromSeconds(30));
            Worker.Dispose();
        }

        static void Try(Action action)
        {
            try { action(); } catch (Exception ex) when (ex is COMException or InvalidComObjectException) { }
        }
    }

    /// <summary>
    /// Word imports PDFs through a separate PDFREFLOW process that shows "Word will now convert your PDF..."
    /// regardless of Word's alert settings. This presses OK on it (never "don't show again") while a PDF converts.
    /// </summary>
    static class PdfImportPrompt
    {
        public static async Task ConfirmUntilAsync(CancellationToken stop)
        {
            var since = DateTime.Now.AddSeconds(-2);
            while (!stop.IsCancellationRequested)
            {
                await Task.Run(() =>
                {
                    foreach (var process in Process.GetProcessesByName("PDFREFLOW"))
                    {
                        using (process)
                        {
                            try
                            {
                                if (process.StartTime >= since) PressOk(process.Id);
                            }
                            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or ElementNotAvailableException) { }
                        }
                    }
                });
                try { await Task.Delay(250, stop); }
                catch (OperationCanceledException) { return; }
            }
        }

        static void PressOk(int processId)
        {
            var dialogs = AutomationElement.RootElement.FindAll(TreeScope.Children, new AndCondition(
                new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
                new PropertyCondition(AutomationElement.ClassNameProperty, "NUIDialog")));
            foreach (AutomationElement dialog in dialogs)
            {
                var buttons = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>().ToList();
                // "OK" in English Office; otherwise the first button, which is the default action.
                var ok = buttons.FirstOrDefault(b => b.Current.Name == "OK") ?? buttons.FirstOrDefault();
                if (ok?.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern) == true)
                    ((InvokePattern)pattern).Invoke();
            }
        }
    }
}
