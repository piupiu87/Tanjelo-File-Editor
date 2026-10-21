using System.Formats.Tar;
using System.IO.Compression;

namespace Tanjelo.Core.Converters;

/// <summary>Extracts archives and repacks them into another archive format.</summary>
public sealed class ArchiveConverter(Toolbox tools) : IConverter
{
    // Single-file compressors (.gz, .bz2, .xz) only make sense to extract.
    static readonly HashSet<string> Repackable = ["zip", "7z", "rar", "tar", "tar.gz", "tar.bz2", "tar.xz", "iso", "cab"];

    public IEnumerable<Target> TargetsFor(string input)
    {
        var key = Formats.Key(input);
        if (!Formats.Archives.Contains(key))
        {
            // Any single file can be gzipped on its own: notes.txt becomes notes.txt.gz.
            yield return new Target("gz", "GZIP", "Compress this file to .gz", Section.Archive) { Running = "Compressing…", Done = "Compressed to GZIP" };
            yield break;
        }
        if (!Archiver.CanExtract(tools, key))
            yield break;

        yield return new Target("extract", "Extract here", "Unpack next to the archive", Section.Archive) { Running = "Extracting…", Done = "Extracted" };
        if (!Repackable.Contains(key))
            yield break;
        yield return new Target("zip", "ZIP", "Opens anywhere", Section.Archive);
        if (tools.SevenZip is not null)
            yield return new Target("7z", "7Z", "Smaller, needs 7-Zip to open", Section.Archive);
        yield return new Target("tar.gz", "TAR.GZ", "Common on Linux and macOS", Section.Archive);
        yield return new Target("tar", "TAR", "Uncompressed, common on Linux", Section.Archive);
    }

    public string? HintFor(string input)
    {
        var key = Formats.Key(input);
        return Formats.Archives.Contains(key) && !Archiver.CanExtract(tools, key) ? "Install 7-Zip to open this kind of archive." : null;
    }

    public async Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
    {
        if (target.Key == "extract")
            return [await ExtractHereAsync(input, ctx)];
        if (target.Key == "gz")
        {
            var gz = OutputPaths.Unique(OutputPaths.DirectoryOf(input), Path.GetFileName(input), "gz");
            await Archiver.GzipAsync(input, gz, ctx.Cancellation, ctx.Progress);
            return [gz];
        }

        using var temp = OutputPaths.CreateTemp();
        await Archiver.ExtractAsync(tools, input, temp.Path, ctx.Cancellation);
        ctx.Progress.Report(0.5);

        var output = OutputPaths.Beside(input, target.Key);
        var halfway = new ActionProgress<double>(f => ctx.Progress.Report(0.5 + f / 2));
        await Archiver.CompressAsync(tools, Directory.GetFileSystemEntries(temp.Path), output, target.Key, ctx.Cancellation, halfway);
        return [output];
    }

    /// <summary>
    /// Like Finder/Explorer: an archive holding a single item extracts to just that item; anything else gets
    /// a folder named after the archive. Extraction happens in a hidden scratch folder first so a failure
    /// never leaves half a folder behind.
    /// </summary>
    async Task<string> ExtractHereAsync(string archive, JobContext ctx)
    {
        var parent = OutputPaths.DirectoryOf(archive);
        var scratch = Path.Combine(parent, ".tanjelo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        File.SetAttributes(scratch, File.GetAttributes(scratch) | FileAttributes.Hidden);
        try
        {
            await Archiver.ExtractAsync(tools, archive, scratch, ctx.Cancellation);
            var entries = Directory.GetFileSystemEntries(scratch);
            if (entries.Length == 1)
            {
                var only = entries[0];
                if (Directory.Exists(only))
                {
                    var dest = OutputPaths.Unique(parent, Path.GetFileName(only), "");
                    await MoveAsync(() => Directory.Move(only, dest), ctx.Cancellation);
                    return dest;
                }
                else
                {
                    var dest = OutputPaths.Unique(parent, Path.GetFileNameWithoutExtension(only), Path.GetExtension(only).TrimStart('.'));
                    await MoveAsync(() => File.Move(only, dest), ctx.Cancellation);
                    return dest;
                }
            }

            var folder = OutputPaths.Unique(parent, Formats.BaseName(archive), "");
            await MoveAsync(() => Directory.Move(scratch, folder), ctx.Cancellation);
            File.SetAttributes(folder, File.GetAttributes(folder) & ~FileAttributes.Hidden);
            return folder;
        }
        finally
        {
            OutputPaths.TryDelete(scratch);
        }
    }

    // Antivirus scanners briefly hold freshly extracted files open, which makes renaming their folder fail.
    static async Task MoveAsync(Action move, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                await Task.Delay(150, ct);
            }
        }
    }
}

/// <summary>Creates archives from any files and folders.</summary>
public sealed class CompressOperation(Toolbox tools) : IBatchOperation
{
    public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs)
    {
        if (inputs.All(i => Formats.KindOf(i) == FileKind.Archive))
            yield break;
        var what = inputs.Count == 1 ? "Compress into" : $"Compress {inputs.Count} items into";
        yield return new Target("compress-zip", "ZIP", $"{what} a .zip", Section.Archive) { IsBatch = true, Running = "Compressing…", Done = "Compressed to ZIP" };
        if (tools.SevenZip is not null)
            yield return new Target("compress-7z", "7Z", $"{what} a .7z (smaller)", Section.Archive) { IsBatch = true, Running = "Compressing…", Done = "Compressed to 7Z" };
        var pack = inputs.Count == 1 ? "Pack into" : $"Pack {inputs.Count} items into";
        yield return new Target("compress-tar", "TAR", $"{pack} a .tar", Section.Archive) { IsBatch = true, Running = "Packing…", Done = "Packed into TAR" };
    }

    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
    {
        var ext = target.Key["compress-".Length..];
        var baseName = inputs.Count == 1 ? Formats.BaseName(inputs[0]) : "Archive";
        var output = OutputPaths.Unique(OutputPaths.DirectoryOf(inputs[0]), baseName, ext);
        await Archiver.CompressAsync(tools, inputs.Select(Path.GetFullPath).ToList(), output, ext, ctx.Cancellation, ctx.Progress);
        return [output];
    }
}

static class Archiver
{
    const string NoPassword = "-ptanjelo-no-password"; // fail fast on encrypted archives instead of prompting

    static bool IsTar(string key) => key.StartsWith("tar", StringComparison.Ordinal);

    /// <summary>7-Zip handles everything; Windows' own tar.exe (libarchive) covers tar, zip, 7z, rar, iso and cab.</summary>
    public static bool CanExtract(Toolbox tools, string key) =>
        tools.SevenZip is not null || tools.Tar is not null || key is "zip" or "gz" or "tar" or "tar.gz";

    public static async Task ExtractAsync(Toolbox tools, string archive, string destination, CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        var key = Formats.Key(archive);

        if (key == "gz")
        {
            await GunzipAsync(archive, Path.Combine(destination, Formats.BaseName(archive)), ct);
        }
        else if (IsTar(key) && tools.Tar is not null)
        {
            // 7-Zip would need two passes for .tar.gz; bsdtar does it in one.
            await RunAsync(tools.Tar, ["-xf", archive, "-C", destination], "tar", ct);
        }
        else if (tools.SevenZip is not null)
        {
            await RunAsync(tools.SevenZip, ["x", "-y", "-bso0", "-bsp0", NoPassword, "-o" + destination, archive], "7-Zip", ct);
        }
        else if (tools.Tar is not null)
        {
            await RunAsync(tools.Tar, ["-xf", archive, "-C", destination], "tar", ct);
        }
        else if (key == "zip")
        {
            await Task.Run(() => ZipFile.ExtractToDirectory(archive, destination), ct);
        }
        else if (key is "tar" or "tar.gz")
        {
            await using var file = File.OpenRead(archive);
            await using Stream source = key == "tar" ? file : new GZipStream(file, CompressionMode.Decompress);
            await TarFile.ExtractToDirectoryAsync(source, destination, overwriteFiles: false, ct);
        }
        else
        {
            throw new ConversionException("Install 7-Zip to open this kind of archive.");
        }
    }

    public static async Task CompressAsync(Toolbox tools, IReadOnlyList<string> items, string output, string format, CancellationToken ct, IProgress<double> progress)
    {
        try
        {
            switch (format)
            {
                case "7z":
                    if (tools.SevenZip is null) throw new ConversionException("Creating .7z files needs 7-Zip.");
                    await RunAsync(tools.SevenZip, ["a", "-t7z", "-mx=7", "-y", "-bso0", "-bsp0", output, .. items], "7-Zip", ct);
                    break;
                case "zip" when tools.SevenZip is not null:
                    await RunAsync(tools.SevenZip, ["a", "-tzip", "-mx=5", "-y", "-bso0", "-bsp0", output, .. items], "7-Zip", ct);
                    break;
                case "zip":
                    await Task.Run(() => WriteZip(items, output, ct, progress), ct);
                    break;
                case "tar.gz" or "tar":
                    await Task.Run(() => WriteTar(items, output, gzip: format == "tar.gz", ct, progress), ct);
                    break;
                default:
                    throw new ConversionException($"Can't create .{format} archives.");
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
        progress.Report(1);
    }

    static async Task RunAsync(string exe, string[] args, string name, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync(exe, args, ct);
        if (result.ExitCode != 0)
        {
            var error = result.LastError;
            if (error.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) || error.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                throw new ConversionException("This archive is password-protected.");
            throw new ConversionException($"{name} failed: {error}");
        }
    }

    static async Task GunzipAsync(string archive, string output, CancellationToken ct)
    {
        await using var source = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress);
        await using var target = File.Create(output);
        await source.CopyToAsync(target, ct);
    }

    public static async Task GzipAsync(string input, string output, CancellationToken ct, IProgress<double> progress)
    {
        try
        {
            await using var source = File.OpenRead(input);
            await using var target = new GZipStream(File.Create(output), CompressionLevel.Optimal);
            var buffer = new byte[1 << 20];
            long done = 0;
            for (int read; (read = await source.ReadAsync(buffer, ct)) > 0;)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                progress.Report((double)done / Math.Max(1, source.Length));
            }
        }
        catch
        {
            OutputPaths.TryDelete(output);
            throw;
        }
    }

    /// <summary>Every file and folder under the items, with archive-style relative names ("Photos/2024/a.jpg").</summary>
    static List<(string Path, string Name, bool IsDirectory, long Size)> Entries(IReadOnlyList<string> items)
    {
        var entries = new List<(string, string, bool, long)>();
        foreach (var item in items)
        {
            var root = Path.GetFileName(item.TrimEnd('\\', '/'));
            if (File.Exists(item))
            {
                entries.Add((item, root, false, new FileInfo(item).Length));
                continue;
            }
            entries.Add((item, root, true, 0));
            foreach (var path in Directory.EnumerateFileSystemEntries(item, "*", SearchOption.AllDirectories))
            {
                var name = root + "/" + Path.GetRelativePath(item, path).Replace('\\', '/');
                var isDir = Directory.Exists(path);
                entries.Add((path, name, isDir, isDir ? 0 : new FileInfo(path).Length));
            }
        }
        return entries;
    }

    static void WriteZip(IReadOnlyList<string> items, string output, CancellationToken ct, IProgress<double> progress)
    {
        var entries = Entries(items);
        var total = Math.Max(1, entries.Sum(e => e.Size));
        long done = 0;
        using var zip = ZipFile.Open(output, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory)
                zip.CreateEntry(entry.Name + "/");
            else
                zip.CreateEntryFromFile(entry.Path, entry.Name, CompressionLevel.Optimal);
            done += entry.Size;
            progress.Report((double)done / total);
        }
    }

    static void WriteTar(IReadOnlyList<string> items, string output, bool gzip, CancellationToken ct, IProgress<double> progress)
    {
        var entries = Entries(items);
        var total = Math.Max(1, entries.Sum(e => e.Size));
        long done = 0;
        using var file = File.Create(output);
        using var stream = gzip ? new GZipStream(file, CompressionLevel.Optimal) : (Stream)file;
        using var tar = new TarWriter(stream, TarEntryFormat.Pax);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            tar.WriteEntry(entry.Path, entry.Name);
            done += entry.Size;
            progress.Report((double)done / total);
        }
    }
}
