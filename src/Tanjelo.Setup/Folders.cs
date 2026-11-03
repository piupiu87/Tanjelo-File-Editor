namespace Tanjelo.Setup;

/// <summary>Comparing and checking folder paths, the same way on .NET Framework and .NET.</summary>
static class Folders
{
    /// <summary>The path without a trailing backslash, except for a drive's root (C:\).</summary>
    public static string Trim(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    public static bool Same(string a, string b) => string.Equals(Trim(a), Trim(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or anything inside it.</summary>
    public static bool Within(string path, string folder) =>
        Same(path, folder) || path.StartsWith(Trim(folder).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A path from a list of installed files, made full, or null when it would point anywhere but inside
    /// <paramref name="folder"/>: rooted, with "..", or with a drive or stream in it.
    /// </summary>
    public static string? Inside(string folder, string relative)
    {
        var parts = relative.Replace('/', '\\').Split('\\');
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0
            || parts.Any(p => p.Length == 0 || p == "." || p == "..") || relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;
        var full = Path.Combine(folder, string.Join("\\", parts));
        return Within(full, folder) && !Same(full, folder) ? full : null;
    }

    /// <summary>Whether <paramref name="path"/> is a junction or a symbolic link (any reparse point), to a folder or a file.</summary>
    public static bool IsLink(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// The first junction or symbolic link that <paramref name="path"/> goes through below <paramref name="top"/>, or null:
    /// a path through one leads to someone else's files, which setup never deletes or moves.
    /// </summary>
    public static string? LinkOnTheWay(string top, string path)
    {
        var folders = new Stack<string>();
        for (var at = Path.GetDirectoryName(Trim(path)); at is not null && Within(at, top) && !Same(at, top); at = Path.GetDirectoryName(at))
            folders.Push(at);
        return folders.FirstOrDefault(IsLink);
    }

    /// <summary>
    /// Deletes <paramref name="folder"/> and the folders above it, up to and including <paramref name="top"/>, while each
    /// is empty. It stops at a link, which isn't setup's to remove.
    /// </summary>
    public static void RemoveEmpty(string folder, string top)
    {
        for (var at = Trim(folder); Within(at, top); at = Path.GetDirectoryName(at) ?? "")
        {
            try
            {
                if (!Directory.Exists(at)) continue;
                if (IsLink(at) || Directory.EnumerateFileSystemEntries(at).Any()) return;
                Directory.Delete(at);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            if (Same(at, top)) return;
        }
    }

    /// <summary>Deletes a file, read-only or not; false if it's still there. A link to a file goes as a link.</summary>
    public static bool DeleteFile(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path) && !IsLink(path)) return true;
                if (!IsLink(path)) File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 4) return false;
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// Deletes a folder setup made for itself, with everything in it, walking it by hand: a junction or symbolic link in
    /// it, or in its place, goes as a link, and what it points to is never looked into.
    /// </summary>
    public static void DeleteOwn(string folder)
    {
        if (IsLink(folder))
        {
            RemoveLink(folder);
            return;
        }
        if (!Directory.Exists(folder)) return;
        foreach (var entry in Directory.GetFileSystemEntries(folder))
        {
            if (IsLink(entry)) RemoveLink(entry);
            else if (Directory.Exists(entry)) DeleteOwn(entry);
            else DeleteFile(entry);
        }
        RemoveEmpty(folder, folder);
    }

    static void RemoveLink(string link)
    {
        try
        {
            // Removing a link to a folder removes the link alone, even when that folder has things in it.
            if ((File.GetAttributes(link) & FileAttributes.Directory) != 0) Directory.Delete(link);
            else File.Delete(link);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The size of a file in bytes as people read it: "310 MB", "1.2 GB".</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{(bytes + (1L << 19)) >> 20} MB",
        _ => $"{Math.Max(1, (bytes + 512) >> 10)} KB",
    };

    /// <summary>Part of a size, as a number in the whole's unit: 124 of 310 MB, 0.4 of 1.2 GB.</summary>
    public static string Part(long bytes, long whole) => whole switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0}",
        >= 1L << 20 => $"{bytes >> 20}",
        _ => $"{bytes >> 10}",
    };
}
