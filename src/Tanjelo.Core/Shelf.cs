using System.Text.Json;

namespace Tanjelo.Core;

/// <summary>
/// The files and folders parked on the Float shelf: references, not copies, in the order they were added. Items whose
/// file was moved or deleted drop off. The list is saved as JSON, so it survives a restart.
/// </summary>
public sealed class Shelf
{
    readonly List<string> _items = [];

    public IReadOnlyList<string> Items => _items;

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

    public bool Remove(IEnumerable<string> paths)
    {
        var gone = paths.Select(Normalize).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = _items.RemoveAll(gone.Contains) > 0;
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        _items.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops the items whose file or folder is gone, such as after they were dragged out and moved; returns those.</summary>
    public IReadOnlyList<string> Prune()
    {
        var missing = _items.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        if (missing.Count > 0)
        {
            _items.RemoveAll(missing.Contains);
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

    /// <summary>The shelf saved in <paramref name="file"/>, without items that are gone; empty when there's no file or it can't be read.</summary>
    public static Shelf Load(string file)
    {
        var shelf = new Shelf();
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
