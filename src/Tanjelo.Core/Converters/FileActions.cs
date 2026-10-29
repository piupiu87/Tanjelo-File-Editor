using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace Tanjelo.Core.Converters;

/// <summary>The Windows clipboard, behind an interface so tests don't overwrite the real one.</summary>
public interface IClipboard
{
    void SetText(string text);

    /// <summary>Puts files on the clipboard the way Explorer's Copy does, ready to paste.</summary>
    void SetFiles(IReadOnlyList<string> paths);
}

public sealed class WindowsClipboard : IClipboard
{
    public void SetText(string text) => OnStaThread(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true));

    public void SetFiles(IReadOnlyList<string> paths) => OnStaThread(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());
        // Tells Explorer to copy rather than move when the files are pasted (DROPEFFECT_COPY).
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        Clipboard.SetDataObject(data, copy: true);
    });

    // The clipboard needs a single-threaded apartment, and the engine runs on the thread pool. copy: true flushes
    // the data to the clipboard, so it stays there after this thread, or the whole program, has exited.
    static void OnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is ExternalException)
            throw new ConversionException(Strings.AnotherAppIsUsingTheClipboardTry, error);
        if (error is not null)
            throw error;
    }
}

/// <summary>Actions that work on the selection itself, for files and folders alike.</summary>
public sealed class ClipboardActions(IClipboard clipboard) : IBatchOperation
{
    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        var one = inputs.Count == 1;
        yield return new Target("copy-path", one ? Strings.CopyPath : Strings.CopyPaths, one ? Strings.FullPathAsText : Strings.OnePathPerLine, Section.File)
        {
            IsBatch = true,
            Kind = TargetKind.Action,
            Running = Strings.Copying,
            Done = one ? Strings.PathCopied : Strings.PathsCopied,
        };
        yield return new Target("copy-files", Strings.Copy, Strings.ReadyToPasteAnywhere, Section.File)
        {
            IsBatch = true,
            Kind = TargetKind.Action,
            Running = Strings.Copying,
            Done = Strings.CopiedReadyToPaste,
        };
    }

    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
    {
        var paths = inputs.Select(p => Path.GetFullPath(p).TrimEnd('\\', '/')).Select(p => p.EndsWith(':') ? p + "\\" : p).ToList();
        if (target.Key == "copy-path")
            clipboard.SetText(string.Join("\r\n", paths));
        else
            clipboard.SetFiles(paths);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}

/// <summary>Opens Windows Terminal (or PowerShell) in the selected folders, or in the folder holding the selected files.</summary>
public sealed class OpenTerminalAction(Toolbox tools) : IBatchOperation
{
    const int MaxFolders = 10;

    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        yield return new Target("open-terminal", Strings.OpenInTerminal, tools.WindowsTerminal is null ? Strings.PowerShell : Strings.WindowsTerminal, Section.File)
        {
            IsBatch = true,
            Kind = TargetKind.Action,
            Running = Strings.Opening,
            Done = Strings.OpenedInTerminal,
        };
    }

    public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
    {
        var folders = Folders(inputs);
        if (folders.Count > MaxFolders)
            throw new ConversionException(string.Format(Strings.ThatSDifferentFoldersTanjeloOpensAt, folders.Count, MaxFolders));
        foreach (var start in StartInfos(folders, tools.WindowsTerminal))
        {
            try
            {
                using var _ = Process.Start(start);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new ConversionException(string.Format(Strings.CouldnTOpen, Path.GetFileName(start.FileName), ex.Message), ex);
            }
        }
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>Each selected folder, and the folder of each selected file, without repeats and in selection order.</summary>
    internal static List<string> Folders(IReadOnlyList<string> inputs) => inputs
        .Select(p => Directory.Exists(p) ? Path.GetFullPath(p) : OutputPaths.DirectoryOf(p))
        .DistinctBy(p => p.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>One Windows Terminal window with a tab per folder, or else one PowerShell window per folder.</summary>
    internal static List<ProcessStartInfo> StartInfos(IReadOnlyList<string> folders, string? windowsTerminal)
    {
        if (windowsTerminal is not null)
        {
            var start = new ProcessStartInfo(windowsTerminal) { UseShellExecute = false };
            // "-w new" keeps it out of any terminal window the user already has open.
            start.ArgumentList.Add("-w");
            start.ArgumentList.Add("new");
            for (var i = 0; i < folders.Count; i++)
            {
                if (i > 0)
                {
                    start.ArgumentList.Add(";");
                    start.ArgumentList.Add("new-tab");
                }
                start.ArgumentList.Add("-d");
                start.ArgumentList.Add(folders[i].Replace(";", "\\;")); // wt reads a bare ";" as "next command"
            }
            return [start];
        }
        // Shell execute gives each its own console window, even when started from tanj's console.
        return folders.Select(f => new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = f, Arguments = "-NoLogo" }).ToList();
    }
}

/// <summary>Moves files and folders into a folder the user picks. Names already taken there get " (2)" and so on.</summary>
public sealed class MoveToFolderAction : IConverter
{
    public bool HandlesFolders => true;

    public IEnumerable<Target> TargetsFor(string input) =>
    [
        new("move-to", Strings.MoveTo, Strings.PickAFolder, Section.File)
        {
            Kind = TargetKind.Action,
            Running = Strings.Moving,
            Done = Strings.Moved,
            FolderPrompt = Strings.MoveToWhichFolder,
            Parameters = new Dictionary<string, string> { ["folder"] = "" },
        },
    ];

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var chosen = target.Parameter("folder");
        if (chosen.Length == 0)
            throw new ConversionException(Strings.ChooseAFolderToMoveTo);
        var destination = Path.GetFullPath(chosen);
        if (!Directory.Exists(destination))
            throw new ConversionException(Strings.ThatFolderDoesnTExist);

        var source = Path.GetFullPath(input).TrimEnd('\\');
        var isFolder = Directory.Exists(source);
        if (string.Equals(OutputPaths.DirectoryOf(source).TrimEnd('\\'), destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new ConversionException(Strings.ItSAlreadyInThatFolder);
        if (isFolder && (destination + "\\").StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
            throw new ConversionException(Strings.AFolderCanTBeMovedInto);

        var output = isFolder ? OutputPaths.Unique(destination, Path.GetFileName(source), "") : FreeFileName(source, destination);
        if (!isFolder)
            File.Move(source, output);
        else if (string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(output), StringComparison.OrdinalIgnoreCase))
            Directory.Move(source, output);
        else
            MoveAcrossDrives(source, output, ctx.Cancellation);
        ctx.Progress.Report(1);
        return [output];
    }, ctx.Cancellation);

    /// <summary>The name a file can take in <paramref name="destination"/>, keeping its extension exactly as written.</summary>
    static string FreeFileName(string source, string destination)
    {
        var name = Path.GetFileName(source);
        var baseName = Formats.BaseName(source);
        var ext = name.Length > baseName.Length ? name[(baseName.Length + 1)..] : "";
        return OutputPaths.Unique(destination, baseName, ext);
    }

    /// <summary>
    /// A folder can't simply be renamed onto another drive, so it's copied and then the original is deleted. If
    /// the copy fails, the partial copy goes and the original stays untouched.
    /// </summary>
    internal static void MoveAcrossDrives(string source, string output, CancellationToken ct)
    {
        try
        {
            CopyFolder(source, output, ct);
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        try
        {
            Directory.Delete(source, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConversionException(string.Format(Strings.CopiedToButSomeOfTheOriginal, output, ex.Message), ex);
        }
    }

    static void CopyFolder(string source, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var folder in Directory.EnumerateDirectories(source))
            CopyFolder(folder, Path.Combine(destination, Path.GetFileName(folder)), ct);
    }
}
