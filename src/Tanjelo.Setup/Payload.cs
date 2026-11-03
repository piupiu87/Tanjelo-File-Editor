using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Tanjelo.Setup;

/// <summary>One file of the app, as it sits in the payload.</summary>
sealed record PayloadFile(string Path, long Length);

/// <summary>
/// The app setup installs: a zip of the published folder, inside Setup.exe, with the SHA-256 the build recorded beside
/// it. The tests make their own.
/// </summary>
sealed class Payload(Func<Stream> open, string sha256, string version)
{
    const string ResourceName = "payload.zip";

    /// <summary>The version of Tanjelo inside, as it shows in Apps.</summary>
    public string Version => version;

    /// <summary>The payload inside this program, or null when there's none: then it's the uninstaller.</summary>
    public static Payload? Embedded(Assembly assembly)
    {
        if (assembly.GetManifestResourceInfo(ResourceName) is null) return null;
        var sha256 = assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute), false).Cast<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "PayloadSha256")?.Value ?? "";
        return new Payload(() => assembly.GetManifestResourceStream(ResourceName)!, sha256, VersionOf(assembly));
    }

    /// <summary>The version a build stamped on <paramref name="assembly"/>, without the commit it was built from.</summary>
    public static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "";
        var plus = informational.IndexOf('+');
        return plus < 0 ? informational : informational.Substring(0, plus);
    }

    IReadOnlyList<PayloadFile>? _files;

    /// <summary>Every file in the payload, with its path inside the install folder.</summary>
    public IReadOnlyList<PayloadFile> Files => _files ??= ReadFiles();

    /// <summary>How much room the installed files take.</summary>
    public long Size => Files.Sum(f => f.Length);

    IReadOnlyList<PayloadFile> ReadFiles()
    {
        using var zip = new ZipArchive(open(), ZipArchiveMode.Read);
        return zip.Entries.Where(IsFile)
            .Select(e => new PayloadFile(Checked(e.FullName), e.Length)).ToList();
    }

    // Zips may list folders too, written with either slash.
    static bool IsFile(ZipArchiveEntry entry) => !entry.FullName.EndsWith("/", StringComparison.Ordinal) && !entry.FullName.EndsWith("\\", StringComparison.Ordinal);

    // A zip entry may only name a file inside the folder it's unpacked into.
    static string Checked(string name)
    {
        var path = name.Replace('/', '\\');
        if (Folders.Inside(@"C:\x", path) is null) throw new SetupException(Strings.SetupSOwnCopyOfTanjeloIsDamaged);
        return path;
    }

    /// <summary>Checks the payload is the one the build made, before anything is copied from it.</summary>
    public void Verify(CancellationToken cancel)
    {
        using var stream = open();
        using var sha = SHA256.Create();
        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }
        sha.TransformFinalBlock(buffer, 0, 0);
        var actual = string.Concat(sha.Hash!.Select(b => b.ToString("x2")));
        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
            throw new SetupException(Strings.SetupSOwnCopyOfTanjeloIsDamaged);
    }

    /// <summary>Unpacks every file into <paramref name="folder"/>, reporting the bytes written so far.</summary>
    public void Extract(string folder, Action<long> progress, CancellationToken cancel)
    {
        using var zip = new ZipArchive(open(), ZipArchiveMode.Read);
        var buffer = new byte[1 << 20];
        long done = 0;
        foreach (var entry in zip.Entries.Where(IsFile))
        {
            var target = Path.Combine(folder, Checked(entry.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = File.Create(target);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                done += read;
                progress(done);
            }
        }
    }
}
