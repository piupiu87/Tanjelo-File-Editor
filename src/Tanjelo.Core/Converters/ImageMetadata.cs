using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ImageMagick;

namespace Tanjelo.Core.Converters;

/// <summary>What to change in a file's metadata. Removals happen first, then the new title and author are added.</summary>
sealed record MetadataEdit(string? Title, string? Author, bool Location, bool Camera, bool All);

/// <summary>
/// The metadata of a JPEG, PNG or WebP file: EXIF, XMP and text notes. It's read from and written back into the
/// file's own structure, so the picture data is copied byte for byte and never re-encoded.
/// </summary>
sealed class ImageMetadata
{
    public static readonly IReadOnlySet<string> Formats = new HashSet<string> { "jpg", "png", "webp" };

    public ExifProfile? Exif { get; private set; }
    public XDocument? Xmp { get; private set; }

    /// <summary>PNG text notes by keyword (Title, Author, Comment…), and a JPEG's comment as "Comment".</summary>
    public Dictionary<string, string> Text { get; } = new(StringComparer.OrdinalIgnoreCase);

    readonly string _format;
    readonly byte[] _file;

    /// <summary>An XMP packet that isn't valid XML: kept as it is, unless it has to be cleaned, when it goes.</summary>
    byte[]? _rawXmp;

    /// <summary>Set by "remove everything": IPTC and the PNG modification time go too.</summary>
    bool _stripped;

    byte[]? XmpBytes => Xmp is not null ? SerializeXmp(Xmp) : _rawXmp;

    ImageMetadata(string format, byte[] file)
    {
        _format = format;
        _file = file;
    }

    public static ImageMetadata Read(string path)
    {
        var format = Core.Formats.Key(path);
        var metadata = new ImageMetadata(format, File.ReadAllBytes(path));
        try
        {
            switch (format)
            {
                case "jpg": metadata.ReadJpeg(); break;
                case "png": metadata.ReadPng(); break;
                case "webp": metadata.ReadWebp(); break;
                default: throw new ConversionException(string.Format(Strings.CanTEditTheMetadataOfFiles, format));
            }
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            throw new ConversionException(string.Format(Strings.ThisDoesnTLookLikeAValid2, format.ToUpperInvariant()), ex);
        }
        return metadata;
    }

    /// <summary>Applies the edit, and says whether anything changed.</summary>
    public bool Apply(MetadataEdit edit)
    {
        var changed = false;
        if (edit.All)
        {
            // Everything goes but the orientation, without which the picture would show sideways.
            var orientation = Exif?.GetValue(ExifTag.Orientation)?.Value;
            changed = Exif is not null || XmpBytes is not null || Text.Count > 0;
            Exif = null;
            Xmp = null;
            _rawXmp = null;
            _stripped = true;
            Text.Clear();
            if (orientation is > 1)
            {
                Exif = new ExifProfile();
                Exif.SetValue(ExifTag.Orientation, orientation.Value);
            }
        }
        if (edit.Location)
            changed |= RemoveExif(name => name.StartsWith("GPS", StringComparison.Ordinal)) | RemoveXmp(Xmp, IsLocation) | RemoveRawProfiles() | DropRawXmp();
        if (edit.Camera)
            changed |= RemoveExif(CameraTags.Contains) | RemoveXmp(Xmp, IsCamera) | RemoveRawProfiles() | DropRawXmp();
        if (edit.Title is { Length: > 0 } title)
        {
            Exif ??= new ExifProfile();
            Exif.SetValue(ExifTag.ImageDescription, title);
            Exif.SetValue(ExifTag.XPTitle, Ucs2(title));
            SetXmp(Dc + "title", title);
            if (_format == "png") Text["Title"] = title;
            changed = true;
        }
        if (edit.Author is { Length: > 0 } author)
        {
            Exif ??= new ExifProfile();
            Exif.SetValue(ExifTag.Artist, author);
            Exif.SetValue(ExifTag.XPAuthor, Ucs2(author));
            SetXmp(Dc + "creator", author);
            if (_format == "png") Text["Author"] = author;
            changed = true;
        }
        return changed;
    }

    public void Write(string output)
    {
        var bytes = _format switch
        {
            "jpg" => WriteJpeg(),
            "png" => WritePng(),
            _ => WriteWebp(),
        };
        File.WriteAllBytes(output, bytes);
    }

    // --- What counts as location and camera details ---

    static readonly HashSet<string> CameraTags =
    [
        "Make", "Model", "SerialNumber", "BodySerialNumber", "CameraSerialNumber", "LensMake", "LensModel", "LensSerialNumber",
        "LensInfo", "LensSpecification", "CameraOwnerName", "OwnerName", "MakerNote",
    ];

    static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    static readonly XNamespace ExifNs = "http://ns.adobe.com/exif/1.0/";
    static readonly XNamespace TiffNs = "http://ns.adobe.com/tiff/1.0/";
    static readonly XNamespace AuxNs = "http://ns.adobe.com/exif/1.0/aux/";
    static readonly XNamespace ExifExNs = "http://cipa.jp/exif/1.0/";

    static bool IsLocation(XName name) => name.Namespace == ExifNs && name.LocalName.StartsWith("GPS", StringComparison.Ordinal);

    static bool IsCamera(XName name) =>
        (name.Namespace == TiffNs && name.LocalName is "Make" or "Model")
        || name.Namespace == AuxNs
        || (name.Namespace == ExifExNs && CameraTags.Contains(name.LocalName));

    bool RemoveExif(Func<string, bool> matches)
    {
        if (Exif is null) return false;
        var doomed = Exif.Values.Where(v => matches(v.Tag.ToString())).Select(v => v.Tag).ToList();
        foreach (var tag in doomed)
            Exif.RemoveValue(tag);
        return doomed.Count > 0;
    }

    /// <summary>XMP properties can be written as elements or as attributes of rdf:Description; both are removed.</summary>
    static bool RemoveXmp(XDocument? xmp, Func<XName, bool> matches)
    {
        if (xmp is null) return false;
        var elements = xmp.Descendants().Where(e => matches(e.Name)).ToList();
        var attributes = xmp.Descendants().Attributes().Where(a => matches(a.Name)).ToList();
        elements.ForEach(e => e.Remove());
        attributes.ForEach(a => a.Remove());
        return elements.Count + attributes.Count > 0;
    }

    bool DropRawXmp()
    {
        var had = _rawXmp is not null;
        _rawXmp = null;
        return had;
    }

    /// <summary>Old ImageMagick writes EXIF and XMP into PNG text notes as hex; they can't be edited, so they go.</summary>
    bool RemoveRawProfiles()
    {
        var raw = Text.Keys.Where(k => k.StartsWith("Raw profile type", StringComparison.OrdinalIgnoreCase)).ToList();
        raw.ForEach(k => Text.Remove(k));
        return raw.Count > 0;
    }

    /// <summary>Puts the new value into an existing XMP title or creator list, so it doesn't contradict the EXIF.</summary>
    void SetXmp(XName name, string value)
    {
        if (Xmp is null) return;
        foreach (var attribute in Xmp.Descendants().Attributes(name).ToList())
            attribute.Value = value;
        foreach (var element in Xmp.Descendants(name))
        {
            var items = element.Descendants(Rdf + "li").ToList();
            if (items.Count == 0)
            {
                element.Value = value;
                continue;
            }
            items.Skip(1).ToList().ForEach(i => i.Remove());
            items[0].Value = value;
        }
    }

    static byte[] Ucs2(string text) => Encoding.Unicode.GetBytes(text + "\0");

    // --- Reading the values people care about ---

    public string? Title => XpText(ExifTag.XPTitle) ?? ExifText(ExifTag.ImageDescription) ?? XmpText(Dc + "title") ?? Text.GetValueOrDefault("Title");
    public string? Author => XpText(ExifTag.XPAuthor) ?? ExifText(ExifTag.Artist) ?? XmpText(Dc + "creator") ?? Text.GetValueOrDefault("Author");
    public string? Taken => ExifText(ExifTag.DateTimeOriginal) ?? ExifText(ExifTag.DateTime) ?? Text.GetValueOrDefault("Creation Time");
    public string? Software => ExifText(ExifTag.Software) ?? Text.GetValueOrDefault("Software");
    public string? Comment => XpText(ExifTag.XPComment) ?? Text.GetValueOrDefault("Comment") ?? Text.GetValueOrDefault("Description");

    public string? Camera =>
        string.Join(" ", new[] { ExifText(ExifTag.Make), ExifText(ExifTag.Model) }.OfType<string>().Distinct()) is { Length: > 0 } camera
            ? camera + (ExifText(ExifTag.LensModel) is { } lens ? $", {lens}" : "")
            : XmpText(TiffNs + "Model");

    /// <summary>"52.3676° N, 4.9041° E", from the EXIF GPS tags or their XMP copies.</summary>
    public string? Location
    {
        get
        {
            var latitude = Degrees(Exif?.GetValue(ExifTag.GPSLatitude)?.Value, Exif?.GetValue(ExifTag.GPSLatitudeRef)?.Value);
            var longitude = Degrees(Exif?.GetValue(ExifTag.GPSLongitude)?.Value, Exif?.GetValue(ExifTag.GPSLongitudeRef)?.Value);
            if (latitude is { } lat && longitude is { } lon)
                return Describe.Place(lat, lon);
            return XmpText(ExifNs + "GPSLatitude") is { } xmpLat ? $"{xmpLat}, {XmpText(ExifNs + "GPSLongitude")}" : null;
        }
    }

    static double? Degrees(Rational[]? parts, string? reference)
    {
        if (parts is not { Length: 3 } || parts.Any(p => p.Denominator == 0)) return null;
        var degrees = parts[0].ToDouble() + parts[1].ToDouble() / 60 + parts[2].ToDouble() / 3600;
        return reference is "S" or "W" ? -degrees : degrees;
    }

    /// <summary>How many other EXIF fields there are, besides the ones shown by name.</summary>
    public int OtherExif => Exif?.Values.Count(v => !Shown.Contains(v.Tag.ToString())
        && !v.Tag.ToString().StartsWith("GPS", StringComparison.Ordinal) && !CameraTags.Contains(v.Tag.ToString())) ?? 0;

    static readonly HashSet<string> Shown = ["XPTitle", "ImageDescription", "XPAuthor", "Artist", "DateTimeOriginal", "DateTime", "Software", "XPComment", "Orientation"];

    string? ExifText(ExifTag<string> tag) => Exif?.GetValue(tag)?.Value?.Trim('\0', ' ') is { Length: > 0 } text ? text : null;

    string? XpText(ExifTag<byte[]> tag) =>
        Exif?.GetValue(tag)?.Value is { Length: > 1 } bytes && Encoding.Unicode.GetString(bytes).TrimEnd('\0') is { Length: > 0 } text ? text : null;

    string? XmpText(XName name)
    {
        if (Xmp is null) return null;
        if (Xmp.Descendants().Attributes(name).FirstOrDefault() is { } attribute) return attribute.Value;
        var element = Xmp.Descendants(name).FirstOrDefault();
        return element is null ? null : (element.Descendants(Rdf + "li").FirstOrDefault()?.Value ?? element.Value).Trim() is { Length: > 0 } text ? text : null;
    }

    // --- JPEG: a sequence of marker segments, then the compressed picture ---

    static readonly byte[] ExifHeader = "Exif\0\0"u8.ToArray();
    static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
    static readonly byte[] XmpExtensionHeader = "http://ns.adobe.com/xmp/extension/\0"u8.ToArray();
    const byte App0 = 0xE0, App1 = 0xE1, App13 = 0xED, Com = 0xFE, Sos = 0xDA;

    /// <summary>The segments before the picture data, each as (marker, payload); and where the picture data starts.</summary>
    List<(byte Marker, byte[] Data)> _segments = [];
    int _imageStart;

    void ReadJpeg()
    {
        if (_file.Length < 4 || _file[0] != 0xFF || _file[1] != 0xD8)
            throw new InvalidDataException();
        var at = 2;
        while (true)
        {
            while (_file[at] == 0xFF && _file[at + 1] == 0xFF) at++; // fill bytes
            if (_file[at] != 0xFF) throw new InvalidDataException();
            var marker = _file[at + 1];
            if (marker == Sos) break;
            var length = BinaryPrimitives.ReadUInt16BigEndian(_file.AsSpan(at + 2));
            var data = _file.AsSpan(at + 4, length - 2).ToArray();
            _segments.Add((marker, data));
            at += 2 + length;
            if (marker == App1 && data.AsSpan().StartsWith(ExifHeader)) Exif = new ExifProfile(data);
            else if (marker == App1 && data.AsSpan().StartsWith(XmpHeader)) ReadXmp(data.AsSpan(XmpHeader.Length));
            else if (marker == Com) Text["Comment"] = Encoding.UTF8.GetString(data).TrimEnd('\0');
        }
        _imageStart = at;
    }

    byte[] WriteJpeg()
    {
        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        // New EXIF and XMP go where the old ones were, or else after the JFIF header that must come first.
        var kept = _segments.Where(s => !IsMetadataSegment(s)).ToList();
        var at = kept.Count > 0 && kept[0].Marker == App0 ? 1 : 0;
        var exif = Exif?.ToByteArray();
        if (exif is not null) kept.Insert(at++, (App1, exif));
        if (XmpBytes is { } xmp) kept.Insert(at, (App1, [.. XmpHeader, .. xmp]));
        if (Text.TryGetValue("Comment", out var comment)) kept.Add((Com, Encoding.UTF8.GetBytes(comment)));
        foreach (var (marker, data) in kept)
        {
            if (data.Length > 65533)
                throw new ConversionException(Strings.TheMetadataIsTooBigToFit);
            output.Write([0xFF, marker]);
            output.Write([(byte)((data.Length + 2) >> 8), (byte)(data.Length + 2)]);
            output.Write(data);
        }
        output.Write(_file.AsSpan(_imageStart));
        return output.ToArray();

        // IPTC (APP13) and the overflow of a large XMP go with "remove everything"; they're rewritten no other way.
        bool IsMetadataSegment((byte Marker, byte[] Data) s) =>
            (s.Marker == App1 && (s.Data.AsSpan().StartsWith(ExifHeader) || s.Data.AsSpan().StartsWith(XmpHeader)))
            || (s.Marker == App1 && Xmp is null && s.Data.AsSpan().StartsWith(XmpExtensionHeader))
            || (s.Marker == App13 && _stripped)
            || s.Marker == Com;
    }

    // --- PNG: a signature, then chunks of length, type, data and checksum ---

    static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    const string XmpKeyword = "XML:com.adobe.xmp";
    List<(string Type, byte[] Data)> _chunks = [];

    void ReadPng()
    {
        if (!_file.AsSpan().StartsWith(PngSignature)) throw new InvalidDataException();
        for (var at = PngSignature.Length; at < _file.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(_file.AsSpan(at));
            var type = Encoding.ASCII.GetString(_file, at + 4, 4);
            var data = _file.AsSpan(at + 8, length).ToArray();
            _chunks.Add((type, data));
            at += 12 + length;
            switch (type)
            {
                case "eXIf":
                    Exif = new ExifProfile([.. ExifHeader, .. data]);
                    break;
                case "tEXt" or "zTXt" or "iTXt":
                    var (keyword, text) = ReadPngText(type, data);
                    if (keyword == XmpKeyword) ReadXmp(Encoding.UTF8.GetBytes(text));
                    else Text[keyword] = text;
                    break;
            }
            if (type == "IEND") break;
        }
    }

    static (string Keyword, string Text) ReadPngText(string type, byte[] data)
    {
        var end = Array.IndexOf(data, (byte)0);
        var keyword = Encoding.Latin1.GetString(data, 0, end);
        switch (type)
        {
            case "tEXt":
                return (keyword, Encoding.Latin1.GetString(data, end + 1, data.Length - end - 1));
            case "zTXt":
                return (keyword, Encoding.Latin1.GetString(Inflate(data.AsSpan(end + 2))));
            default:
                // iTXt: keyword, compressed?, method, language, translated keyword, then UTF-8 text.
                var compressed = data[end + 1] == 1;
                var language = Array.IndexOf(data, (byte)0, end + 3);
                var translated = Array.IndexOf(data, (byte)0, language + 1);
                var text = data.AsSpan(translated + 1);
                return (keyword, Encoding.UTF8.GetString(compressed ? Inflate(text) : text));
        }
    }

    static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        using var input = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    byte[] WritePng()
    {
        var metadata = new List<(string Type, byte[] Data)>();
        if (Exif?.ToByteArray() is { } exif) metadata.Add(("eXIf", exif[ExifHeader.Length..]));
        if (XmpBytes is { } xmp) metadata.Add(("iTXt", PngText(XmpKeyword, Encoding.UTF8.GetString(xmp), international: true)));
        foreach (var (keyword, text) in Text)
            metadata.Add(text.All(c => c < 256) ? ("tEXt", PngText(keyword, text, international: false)) : ("iTXt", PngText(keyword, text, international: true)));

        using var output = new MemoryStream();
        output.Write(PngSignature);
        var written = false;
        foreach (var chunk in _chunks)
        {
            if (chunk.Type is "eXIf" or "tEXt" or "zTXt" or "iTXt" || (chunk.Type == "tIME" && _stripped)) continue; // rewritten below, or dropped
            // Metadata goes before the picture data, where eXIf has to be.
            if (!written && chunk.Type is "IDAT" or "IEND")
            {
                metadata.ForEach(m => WriteChunk(output, m.Type, m.Data));
                written = true;
            }
            WriteChunk(output, chunk.Type, chunk.Data);
        }
        return output.ToArray();
    }

    /// <summary>A text note: tEXt for plain Latin-1, iTXt (uncompressed UTF-8) otherwise.</summary>
    static byte[] PngText(string keyword, string text, bool international) => international
        ? [.. Encoding.Latin1.GetBytes(keyword), 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(text)]
        : [.. Encoding.Latin1.GetBytes(keyword), 0, .. Encoding.Latin1.GetBytes(text)];

    static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(typed));
        output.Write(length);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    // --- WebP: a RIFF file of chunks; metadata needs the extended header (VP8X), whose flags list it ---

    List<(string FourCC, byte[] Data)> _riff = [];

    void ReadWebp()
    {
        if (_file.Length < 12 || Encoding.ASCII.GetString(_file, 0, 4) != "RIFF" || Encoding.ASCII.GetString(_file, 8, 4) != "WEBP")
            throw new InvalidDataException();
        for (var at = 12; at + 8 <= _file.Length;)
        {
            var fourCC = Encoding.ASCII.GetString(_file, at, 4);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(_file.AsSpan(at + 4));
            var data = _file.AsSpan(at + 8, size).ToArray();
            _riff.Add((fourCC, data));
            at += 8 + size + (size & 1);
            if (fourCC == "EXIF")
                Exif = new ExifProfile(data.AsSpan().StartsWith(ExifHeader) ? data : [.. ExifHeader, .. data]);
            else if (fourCC == "XMP ")
                ReadXmp(data);
        }
    }

    byte[] WriteWebp()
    {
        var chunks = _riff.Where(c => c.FourCC is not ("EXIF" or "XMP ")).ToList();
        var exif = Exif?.ToByteArray()?[ExifHeader.Length..];
        var xmp = XmpBytes;
        if ((exif is not null || xmp is not null) && chunks[0].FourCC != "VP8X")
            chunks.Insert(0, ("VP8X", ExtendedHeader(chunks[0])));
        if (chunks[0].FourCC == "VP8X")
        {
            var header = chunks[0].Data.ToArray();
            header[0] = (byte)((header[0] & ~0x0C) | (exif is not null ? 0x08 : 0) | (xmp is not null ? 0x04 : 0));
            chunks[0] = ("VP8X", header);
        }
        if (exif is not null) chunks.Add(("EXIF", exif));
        if (xmp is not null) chunks.Add(("XMP ", xmp));

        Span<byte> size = stackalloc byte[4];
        using var body = new MemoryStream();
        body.Write("WEBP"u8);
        foreach (var (fourCC, data) in chunks)
        {
            body.Write(Encoding.ASCII.GetBytes(fourCC));
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)data.Length);
            body.Write(size);
            body.Write(data);
            if ((data.Length & 1) == 1) body.WriteByte(0);
        }
        using var output = new MemoryStream();
        output.Write("RIFF"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        output.Write(size);
        body.WriteTo(output);
        return output.ToArray();
    }

    /// <summary>The VP8X header a simple WebP needs before it can carry metadata, with the canvas size from its picture.</summary>
    static byte[] ExtendedHeader((string FourCC, byte[] Data) image)
    {
        int width, height;
        var alpha = false;
        var d = image.Data;
        if (image.FourCC == "VP8 " && d.Length >= 10 && d[3] == 0x9D && d[4] == 0x01 && d[5] == 0x2A)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(6)) & 0x3FFF;
            height = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(8)) & 0x3FFF;
        }
        else if (image.FourCC == "VP8L" && d.Length >= 5 && d[0] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(1));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            alpha = ((bits >> 28) & 1) == 1;
        }
        else
        {
            throw new ConversionException(Strings.ThisWebPFileIsLaidOutIn);
        }
        var header = new byte[10];
        header[0] = (byte)(alpha ? 0x10 : 0);
        header[4] = (byte)(width - 1);
        header[5] = (byte)((width - 1) >> 8);
        header[6] = (byte)((width - 1) >> 16);
        header[7] = (byte)(height - 1);
        header[8] = (byte)((height - 1) >> 8);
        header[9] = (byte)((height - 1) >> 16);
        return header;
    }

    // --- XMP packets ---

    void ReadXmp(ReadOnlySpan<byte> packet)
    {
        try
        {
            Xmp = XDocument.Parse(Encoding.UTF8.GetString(packet).TrimEnd('\0', ' ', '\n', '\r'), LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            _rawXmp = packet.ToArray();
        }
    }

    static byte[] SerializeXmp(XDocument xmp)
    {
        var settings = new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = System.Xml.XmlWriter.Create(stream, settings))
            xmp.Save(writer);
        return stream.ToArray();
    }
}
