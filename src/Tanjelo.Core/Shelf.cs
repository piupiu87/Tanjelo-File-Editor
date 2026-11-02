using System.Text.Json;

namespace Tanjelo.Core;

/// <summary>
/// The files and folders parked on the Float shelf, in the order they were added. Most are references, not copies, and
/// drop off when their file is moved or deleted. Pictures taken from the clipboard, and their results, are files the
/// shelf holds in Tanjelo's own folder: taking one off sends its file to the Recycle Bin. The list is saved as JSON, so
/// it survives a restart.
/// </summary>
public sealed class Shelf
{
    readonly List<string> _items = [];
    readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<IReadOnlyList<string>> _recycle;

    /// <param name="folder">Tanjelo's folder for pictures from the clipboard, whose files the shelf holds.</param>
    /// <param name="recycle">Sends files to the Recycle Bin.</param>
    public Shelf(ClipboardFolder? folder = null, Action<IReadOnlyList<string>>? recycle = null)
    {
        Folder = folder;
        _recycle = recycle ?? (_ => { });
    }

    public IReadOnlyList<string> Items => _items;

    public ClipboardFolder? Folder { get; }

    /// <summary>Whether the shelf holds the file itself, in Tanjelo's folder, rather than pointing at one of the user's.</summary>
    public bool Holds(string path) => Folder?.Holds(path) == true;

    /// <summary>Whether the shelf was showing when it was saved, so it can come back the same way.</summary>
    public bool Open { get; set; }

    public event Action? Changed;

    /// <summary>Adds the paths that exist and aren't on the shelf yet, and returns those.</summary>
    public IReadOnlyList<string> Add(IEnumerable<string> paths)
    {
        var added = new List<string>();
        foreach (var path in paths)
        {
            var full = Normalize(path);
            if (full is null || !(File.Exists(full) || Directory.Exists(full)) || Contains(full)) continue;
            _items.Add(full);
            added.Add(full);
        }
        if (added.Count > 0) Changed?.Invoke();
        return added;
    }

    public bool Contains(string path) => Normalize(path) is { } full && _items.Contains(full, StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes items off the shelf; the files it holds go to the Recycle Bin, the user's own stay where they are.</summary>
    public bool Remove(IEnumerable<string> paths)
    {
        var gone = paths.Select(Normalize).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = _items.Where(gone.Contains).ToList();
        if (removed.Count == 0) return false;
        _items.RemoveAll(gone.Contains);
        Recycle(removed);
        Changed?.Invoke();
        return true;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        var removed = _items.ToList();
        _items.Clear();
        Recycle(removed);
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts <paramref name="replacement"/> in the place of <paramref name="old"/>, such as a screenshot copied again after
    /// an edit in Snipping Tool; the old file goes to the Recycle Bin when the shelf holds it.
    /// </summary>
    public bool Replace(string old, string replacement)
    {
        var at = Normalize(old) is { } full ? _items.FindIndex(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase)) : -1;
        if (at < 0 || Normalize(replacement) is not { } added || !(File.Exists(added) || Directory.Exists(added))) return false;
        if (Contains(added)) return Remove([_items[at]]);
        var before = _items[at];
        _items[at] = added;
        Recycle([before]);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Notes that items were used, by dragging, copying, saving, editing or converting them. Kept in memory only.</summary>
    public void MarkUsed(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (Normalize(path) is { } full) _used.Add(full);
    }

    public bool IsUsed(string path) => Normalize(path) is { } full && _used.Contains(full);

    void Recycle(IEnumerable<string> removed)
    {
        var list = removed.ToList();
        _used.ExceptWith(list);
        var held = list.Where(p => Holds(p) && (File.Exists(p) || Directory.Exists(p))).ToList();
        if (held.Count > 0) _recycle(held);
    }

    /// <summary>Drops the items whose file or folder is gone, such as after they were dragged out and moved; returns those.</summary>
    public IReadOnlyList<string> Prune()
    {
        var missing = _items.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            _items.RemoveAll(missing.Contains);
            _used.ExceptWith(missing);
            Changed?.Invoke();
        }
        return missing;
    }

    sealed record Saved(List<string> Items, bool Open);

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public void Save(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, JsonSerializer.Serialize(new Saved([.. _items], Open), Json));
    }

    /// <summary>
    /// The shelf saved in <paramref name="file"/>, without items that are gone; empty when there's no file or it can't be
    /// read. Nothing is recycled: what's still listed comes back.
    /// </summary>
    public static Shelf Load(string file, ClipboardFolder? folder = null, Action<IReadOnlyList<string>>? recycle = null)
    {
        var shelf = new Shelf(folder, recycle);
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(file), Json) is { } saved)
            {
                shelf.Add(saved.Items ?? []);
                shelf.Open = saved.Open && shelf._items.Count > 0;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged file just means an empty shelf.
        }
        return shelf;
    }

    static string? Normalize(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full; // keeps "C:\"
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
