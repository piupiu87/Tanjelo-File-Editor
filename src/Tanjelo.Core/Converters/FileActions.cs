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

    /// <summary>
    /// Puts a picture on the clipboard for chats, documents and Paint (as a PNG and as bitmaps) and, alongside, the file
    /// itself for Explorer.
    /// </summary>
    void SetPicture(string file);

    /// <summary>What's on the clipboard that Tanjelo can use. Throws <see cref="ConversionException"/> when another app holds it.</summary>
    ClipboardData Read();

    /// <summary>A number that changes each time anything is copied.</summary>
    uint Sequence { get; }
}

public sealed class WindowsClipboard : IClipboard
{
    /// <summary>WPF's name for CF_DIBV5, which it has none of its own for; asking by number first ties the name to it.</summary>
    static readonly string DibV5 = DataFormats.GetDataFormat((int)ClipboardData.CF_DIBV5).Name;

    public void SetText(string text) => OnStaThread(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), copy: true));

    public void SetFiles(IReadOnlyList<string> paths) => OnStaThread(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.FileDrop, paths.ToArray());
        // Tells Explorer to copy rather than move when the files are pasted (DROPEFFECT_COPY).
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        Clipboard.SetDataObject(data, copy: true);
    });

    public void SetPicture(string file)
    {
        var data = PictureData(file); // decoded here, off the clipboard's thread
        OnStaThread(() => Clipboard.SetDataObject(data, copy: true));
    }

    /// <summary>What <see cref="SetPicture"/> puts on the clipboard: PNG, CF_DIBV5, CF_DIB, and the file to paste as a copy.</summary>
    public static DataObject PictureData(string file)
    {
        var (png, dibV5, dib) = ClipboardPicture.ClipboardFormats(file);
        var data = new DataObject();
        // Stored as they are: WPF would otherwise offer bitmap formats made from them that it can't make from bytes.
        data.SetData("PNG", new MemoryStream(png), autoConvert: false);
        data.SetData(DibV5, new MemoryStream(dibV5), autoConvert: false);
        data.SetData(DataFormats.Dib, new MemoryStream(dib), autoConvert: false);
        data.SetData(DataFormats.FileDrop, new[] { Path.GetFullPath(file) });
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1)));
        return data;
    }

    public uint Sequence => GetClipboardSequenceNumber();

    /// <summary>Whether copied files or a picture are on the clipboard, asked without opening it, as the tray menu opens.</summary>
    public static bool HasPictureOrFiles() =>
        new[] { ClipboardData.CF_HDROP, ClipboardData.CF_DIBV5, ClipboardData.CF_DIB, ClipboardData.CF_BITMAP, RegisterClipboardFormat("PNG") }
            .Any(IsClipboardFormatAvailable);

    public ClipboardData Read() => OnStaThread(() =>
    {
        // Another app may have it open for a moment, as every app does while it copies.
        for (var attempt = 1; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt == 5) throw new ConversionException(Strings.AnotherAppIsUsingTheClipboardTry);
            Thread.Sleep(50);
        }
        try
        {
            // The bytes are copied out and the clipboard closed at once; the picture is decoded afterwards. A bitmap is
            // read only without a PNG, since asking an app for a format it renders late makes it do the work there and
            // then. The picture is read even beside files, for a screenshot tool that puts both.
            var formats = new List<uint>();
            for (var format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
                formats.Add(format);
            var files = formats.Contains(ClipboardData.CF_HDROP) ? DroppedFiles() : [];
            var pngFormat = RegisterClipboardFormat("PNG");
            var png = formats.Contains(pngFormat) ? Bytes(pngFormat) : null;
            if (png is not null && ClipboardPicture.IsPng(png))
                return new ClipboardData { Files = files, Png = png };
            var own = ClipboardData.OwnBitmap(formats);
            return new ClipboardData { Files = files, Png = png, Bitmap = own == 0 ? null : Bytes(own) };
        }
        finally
        {
            CloseClipboard();
        }
    });

    static byte[]? Bytes(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        var size = (long)GlobalSize(handle);
        var pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero || size is <= 0 or > int.MaxValue) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    static List<string> DroppedFiles()
    {
        var drop = GetClipboardData(ClipboardData.CF_HDROP);
        var files = new List<string>();
        if (drop == IntPtr.Zero) return files;
        var count = DragQueryFile(drop, uint.MaxValue, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var name = new char[DragQueryFile(drop, i, null, 0) + 1];
            if (DragQueryFile(drop, i, name, (uint)name.Length) > 0)
                files.Add(new string(name, 0, name.Length - 1));
        }
        return files;
    }

    // The clipboard needs a single-threaded apartment, and the engine runs on the thread pool. copy: true flushes
    // the data to the clipboard, so it stays there after this thread, or the whole program, has exited.
    static void OnStaThread(Action action) => OnStaThread(() =>
    {
        action();
        return true;
    });

    static T OnStaThread<T>(Func<T> work)
    {
        Exception? error = null;
        T result = default!;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is ExternalException)
            throw new ConversionException(Strings.AnotherAppIsUsingTheClipboardTry, error);
        if (error is not null)
            throw error;
        return result;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll")]
    static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern UIntPtr GlobalSize(IntPtr memory);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint DragQueryFile(IntPtr drop, uint index, char[]? file, uint size);
}

/// <summary>
/// How results go on the clipboard, by hand from a card or by themselves for a picture taken from the clipboard: one
/// picture as a picture (and its file), one text file as its text, anything else as files to paste.
/// </summary>
public static class ClipboardResults
{
    /// <summary>A longer text file goes on as the file: pasting a megabyte of text is seldom what's meant.</summary>
    public const long MaxText = 1024 * 1024;

    static readonly HashSet<string> Pictures = ["png", "jpg", "jpeg", "jfif", "bmp", "gif", "webp", "tif", "tiff"];

    /// <summary>Whether a file goes on the clipboard as a picture: the kinds chats, documents and Paint take.</summary>
    public static bool IsPicture(string path) => Pictures.Contains(Formats.Ext(path)) && File.Exists(path);

    public static void Copy(IClipboard clipboard, IReadOnlyList<string> outputs)
    {
        if (outputs is [var one] && IsPicture(one))
        {
            try
            {
                clipboard.SetPicture(one);
                return;
            }
            catch (ImageMagick.MagickException)
            {
                // A picture that can't be read goes on as the file.
            }
        }
        else if (outputs is [var text] && Formats.Ext(text) == "txt" && File.Exists(text) && new FileInfo(text).Length < MaxText)
        {
            clipboard.SetText(File.ReadAllText(text));
            return;
        }
        clipboard.SetFiles(outputs);
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
    internal static string FreeFileName(string source, string destination)
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

    internal static void CopyFolder(string source, string destination, CancellationToken ct)
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

/// <summary>
/// Copies files and folders into a folder the user picks, leaving them where they are, as Save to… on a picture taken
/// from the clipboard does. Names already taken there get " (2)" and so on.
/// </summary>
public sealed class SaveToFolderAction : IConverter
{
    public bool HandlesFolders => true;

    public IEnumerable<Target> TargetsFor(string input) =>
    [
        new("save-to", Strings.SaveTo, Strings.CopyIntoAFolderYouPick, Section.File)
        {
            Kind = TargetKind.Action,
            Running = Strings.Saving,
            Done = Strings.Saved,
            FolderPrompt = Strings.SaveToWhichFolder,
            Parameters = new Dictionary<string, string> { ["folder"] = "" },
        },
    ];

    /// <summary>Save in "Folder": the entry with the folder already chosen, such as the one open in Explorer.</summary>
    public static Target In(Target saveTo, string folder) => saveTo.WithParameters(new Dictionary<string, string> { ["folder"] = folder }) with
    {
        Label = string.Format(Strings.SaveIn, Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } name ? name : folder),
        Description = folder,
        FolderPrompt = null,
    };

    public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.Run<IReadOnlyList<string>>(() =>
    {
        var chosen = target.Parameter("folder");
        if (chosen.Length == 0)
            throw new ConversionException(Strings.ChooseAFolderToSaveTo);
        var output = CopyInto(input, chosen, ctx.Cancellation);
        ctx.Progress.Report(1);
        return [output];
    }, ctx.Cancellation);

    /// <summary>Copies a file or folder into <paramref name="folder"/> under a name not yet taken there, and returns the copy.</summary>
    public static string CopyInto(string input, string folder, CancellationToken ct = default)
    {
        var destination = Path.GetFullPath(folder);
        if (!Directory.Exists(destination))
            throw new ConversionException(Strings.ThatFolderDoesnTExist);
        var source = Path.GetFullPath(input).TrimEnd('\\');
        var isFolder = Directory.Exists(source);
        if (string.Equals(OutputPaths.DirectoryOf(source).TrimEnd('\\'), destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new ConversionException(Strings.ItSAlreadyInThatFolder);
        if (isFolder && (destination + "\\").StartsWith(source + "\\", StringComparison.OrdinalIgnoreCase))
            throw new ConversionException(Strings.AFolderCanTBeCopiedIntoItself);

        if (!isFolder)
        {
            var output = MoveToFolderAction.FreeFileName(source, destination);
            File.Copy(source, output);
            return output;
        }
        var copy = OutputPaths.Unique(destination, Path.GetFileName(source), "");
        try
        {
            MoveToFolderAction.CopyFolder(source, copy, ct);
        }
        catch
        {
            OutputPaths.TryDelete(copy);
            throw;
        }
        return copy;
    }
}
