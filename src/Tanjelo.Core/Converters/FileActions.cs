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
            throw new ConversionException("Another app is using the clipboard. Try again in a moment.", error);
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
        yield return new Target("copy-path", one ? "Copy path" : "Copy paths", one ? "The full path, to paste anywhere" : "One full path per line", Section.File)
        {
            IsBatch = true,
            Kind = TargetKind.Action,
            Running = "Copying…",
            Done = one ? "Path copied" : "Paths copied",
        };
        yield return new Target("copy-files", "Copy", "Paste into another folder, an email or a chat", Section.File)
        {
            IsBatch = true,
            Kind = TargetKind.Action,
            Running = "Copying…",
            Done = "Copied, ready to paste",
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
