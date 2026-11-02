using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Tanjelo.Core;

/// <summary>
/// Tanjelo's own folder for pictures taken from the clipboard, %LOCALAPPDATA%\Tanjelo\Clipboard: not %TEMP%, which
/// Storage Sense empties while the shelf still lists them, and inside the installer's folder, so uninstalling removes
/// it. Every file in it belongs to the shelf. Results of those files are written beside them, so they're held here too.
/// </summary>
public sealed class ClipboardFolder
{
    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tanjelo", "Clipboard");

    readonly Func<DateTime> _now;
    readonly Lock _saving = new();
    readonly Dictionary<string, (long Length, DateTime Written, byte[] Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);
    (uint Sequence, string File)? _saved;

    /// <param name="now">The clock pictures are named by.</param>
    public ClipboardFolder(string path, Func<DateTime> now)
    {
        Path = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        _now = now;
    }

    public string Path { get; }

    /// <summary>Whether a file or folder is inside this folder, so the shelf holds it rather than pointing at it.</summary>
    public bool Holds(string path)
    {
        try
        {
            return System.IO.Path.GetFullPath(path).StartsWith(Path + "\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Saves the picture copied as <paramref name="sequence"/> (the clipboard's number for it), named as Windows names
    /// screenshots, and returns the file and whether it's new. The same copy saved before, or a picture here with the
    /// same pixels, is that file again rather than "(2)".
    /// </summary>
    public (string File, bool New) Save(ClipboardPicture picture, uint sequence)
    {
        lock (_saving)
        {
            if (_saved is { } saved && saved.Sequence == sequence && File.Exists(saved.File))
                return (saved.File, false);
            Directory.CreateDirectory(Path);
            var hash = Hash(picture.Png);
            var same = Directory.EnumerateFiles(Path, "*.png")
                .FirstOrDefault(f => SizeOf(f) == (picture.Width, picture.Height) && HashOf(f)?.AsSpan().SequenceEqual(hash) == true);
            var name = string.Format(System.Globalization.CultureInfo.InvariantCulture, Strings.ScreenshotFileName, _now());
            var file = same ?? OutputPaths.Unique(Path, name, "png");
            if (same is null)
            {
                File.WriteAllBytes(file, picture.Png);
                Remember(file, hash);
            }
            _saved = (sequence, file);
            return (file, same is null);
        }
    }

    /// <summary>The files and folders here that aren't on the shelf, such as after a crash or a menu closed with Esc.</summary>
    public IReadOnlyList<string> Orphans(IEnumerable<string> onShelf)
    {
        if (!Directory.Exists(Path)) return [];
        var kept = onShelf.Select(p => System.IO.Path.GetFullPath(p).TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFileSystemEntries(Path).Where(p => !kept.Contains(p)).ToList();
    }

    /// <summary>The width and height in a PNG's header, without reading the rest.</summary>
    static (int, int)? SizeOf(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            var header = new byte[33];
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length || !ClipboardPicture.IsPng(header))
                return null;
            return (BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A file's pixels, hashed; worked out once for each version of the file.</summary>
    byte[]? HashOf(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (_hashes.TryGetValue(file, out var known) && known.Length == info.Length && known.Written == info.LastWriteTimeUtc)
                return known.Hash;
            var hash = Hash(File.ReadAllBytes(file));
            _hashes[file] = (info.Length, info.LastWriteTimeUtc, hash);
            return hash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ImageMagick.MagickException)
        {
            return null;
        }
    }

    void Remember(string file, byte[] hash)
    {
        var info = new FileInfo(file);
        _hashes[file] = (info.Length, info.LastWriteTimeUtc, hash);
    }

    /// <summary>A hash of a PNG's pixels, so the same picture compressed differently still matches.</summary>
    static byte[] Hash(byte[] png)
    {
        using var image = new ImageMagick.MagickImage(png);
        using var pixels = image.GetPixelsUnsafe();
        return SHA256.HashData(pixels.ToByteArray("BGRA")!);
    }
}
