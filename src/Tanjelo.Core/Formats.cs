namespace Tanjelo.Core;

public enum FileKind { Unknown, Folder, Image, Video, Audio, Pdf, WordDocument, Presentation, Spreadsheet, Archive }

public static class Formats
{
    public static readonly IReadOnlySet<string> Images = Set("png", "jpg", "jpeg", "jfif", "webp", "bmp", "gif", "tif", "tiff", "ico", "heic", "heif", "avif", "tga", "psd", "jxl", "svg");
    public static readonly IReadOnlySet<string> Videos = Set("mp4", "mov", "mkv", "webm", "avi", "wmv", "flv", "m4v", "mpg", "mpeg", "3gp", "ts", "mts", "m2ts");
    public static readonly IReadOnlySet<string> Audio = Set("mp3", "wav", "flac", "m4a", "aac", "ogg", "oga", "opus", "wma", "aif", "aiff");
    public static readonly IReadOnlySet<string> WordDocuments = Set("doc", "docx", "docm", "dotx", "rtf", "odt", "txt");
    public static readonly IReadOnlySet<string> Presentations = Set("ppt", "pptx", "pptm", "ppsx", "odp");
    public static readonly IReadOnlySet<string> Spreadsheets = Set("xls", "xlsx", "xlsm", "xlsb", "ods", "csv");
    public static readonly IReadOnlySet<string> Archives = Set("zip", "7z", "rar", "tar", "tar.gz", "tar.bz2", "tar.xz", "gz", "bz2", "xz", "iso", "cab");

    // Suffix -> canonical extension, checked before the plain extension so "x.tar.gz" isn't read as "gz".
    static readonly (string Suffix, string Ext)[] Compound =
    [
        ("tar.gz", "tar.gz"), ("tgz", "tar.gz"),
        ("tar.bz2", "tar.bz2"), ("tbz2", "tar.bz2"),
        ("tar.xz", "tar.xz"), ("txz", "tar.xz"),
    ];

    /// <summary>Lower-case extension without the dot, e.g. "png" or "tar.gz".</summary>
    public static string Ext(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        foreach (var (suffix, ext) in Compound)
            if (name.EndsWith("." + suffix, StringComparison.Ordinal))
                return ext;
        return Path.GetExtension(name).TrimStart('.');
    }

    /// <summary>File name without its (possibly compound) extension; folders keep their full name.</summary>
    public static string BaseName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (Directory.Exists(path))
            return name;
        var lower = name.ToLowerInvariant();
        foreach (var (suffix, _) in Compound)
            if (lower.EndsWith("." + suffix, StringComparison.Ordinal))
                return name[..^(suffix.Length + 1)];
        return Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>Maps aliases to one key so "jpeg" and "jpg" count as the same format.</summary>
    public static string Normalize(string ext) => ext switch
    {
        "jpeg" or "jfif" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        "aif" => "aiff",
        "oga" => "ogg",
        "mpeg" => "mpg",
        _ => ext,
    };

    public static string Key(string path) => Normalize(Ext(path));

    public static FileKind KindOf(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        var ext = Ext(path);
        if (ext == "pdf") return FileKind.Pdf;
        if (Images.Contains(ext)) return FileKind.Image;
        if (Videos.Contains(ext)) return FileKind.Video;
        if (Audio.Contains(ext)) return FileKind.Audio;
        if (WordDocuments.Contains(ext)) return FileKind.WordDocument;
        if (Presentations.Contains(ext)) return FileKind.Presentation;
        if (Spreadsheets.Contains(ext)) return FileKind.Spreadsheet;
        if (Archives.Contains(ext)) return FileKind.Archive;
        return FileKind.Unknown;
    }

    static IReadOnlySet<string> Set(params string[] items) => new HashSet<string>(items, StringComparer.OrdinalIgnoreCase);
}
