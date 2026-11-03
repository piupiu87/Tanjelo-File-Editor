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

    /// <summary>Deletes <paramref name="folder"/> and the folders above it, up to and including <paramref name="top"/>, while each is empty.</summary>
    public static void RemoveEmpty(string folder, string top)
    {
        for (var at = Trim(folder); Within(at, top); at = Path.GetDirectoryName(at) ?? "")
        {
            try
            {
                if (!Directory.Exists(at)) continue;
                if (Directory.EnumerateFileSystemEntries(at).Any()) return;
                Directory.Delete(at);
            }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            if (Same(at, top)) return;
        }
    }

    /// <summary>Every empty folder under <paramref name="top"/>, deepest first, then <paramref name="top"/> itself if that leaves it empty.</summary>
    public static void RemoveAllEmpty(string top)
    {
        if (!Directory.Exists(top)) return;
        foreach (var folder in Directory.EnumerateDirectories(top, "*", SearchOption.AllDirectories).OrderByDescending(f => f.Length))
            RemoveEmpty(folder, folder);
        RemoveEmpty(top, top);
    }

    /// <summary>Deletes a file, read-only or not; false if it's still there.</summary>
    public static bool DeleteFile(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal);
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

    /// <summary>Deletes a folder setup made for itself, with everything in it.</summary>
    public static void DeleteOwn(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            DeleteFile(file);
        RemoveAllEmpty(folder);
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
