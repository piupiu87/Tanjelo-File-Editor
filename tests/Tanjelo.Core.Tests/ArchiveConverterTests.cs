using System.IO.Compression;

namespace Tanjelo.Core.Tests;

public class ArchiveConverterTests
{
    [Fact]
    public async Task Files_and_folders_compress_into_one_zip()
    {
        using var scratch = new Scratch();
        var folder = scratch.Folder("photos", "a.txt", "b.txt");
        var file = scratch.Text("notes.txt", "hello");

        var output = await Env.RunOne("compress-zip", folder, file);

        Assert.Equal(scratch.PathOf("Archive.zip"), output);
        using var zip = ZipFile.OpenRead(output);
        Assert.Equal(["notes.txt", "photos/", "photos/a.txt", "photos/b.txt"], zip.Entries.Select(e => e.FullName).Order());
    }

    [Fact]
    public async Task One_item_compresses_into_a_zip_named_after_it()
    {
        using var scratch = new Scratch();
        var output = await Env.RunOne("compress-zip", scratch.Folder("photos", "a.txt"));
        Assert.Equal(scratch.PathOf("photos.zip"), output);
    }

    [Fact]
    public async Task A_zip_with_one_file_extracts_to_just_that_file()
    {
        using var scratch = new Scratch();
        var zip = Zip(scratch, "single.zip", "report.txt");

        var output = await Env.RunOne("extract", zip);

        Assert.Equal(scratch.PathOf("report.txt"), output);
        Assert.Equal("contents of report.txt", File.ReadAllText(output));
    }

    [Fact]
    public async Task A_zip_with_several_files_extracts_to_a_folder_named_after_it()
    {
        using var scratch = new Scratch();
        var zip = Zip(scratch, "bundle.zip", "a.txt", "b.txt");

        var output = await Env.RunOne("extract", zip);

        Assert.Equal(scratch.PathOf("bundle"), output);
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFiles(output).Select(Path.GetFileName).Order());
        Assert.DoesNotContain(scratch.Files(), f => f.StartsWith(".tanjelo-"));
    }

    [Fact]
    public async Task Extracting_never_overwrites_an_existing_folder()
    {
        using var scratch = new Scratch();
        var zip = Zip(scratch, "bundle.zip", "a.txt", "b.txt");
        scratch.Folder("bundle");

        Assert.Equal(scratch.PathOf("bundle (2)"), await Env.RunOne("extract", zip));
    }

    [Fact]
    public async Task A_zip_repacks_as_tar_gz_and_back()
    {
        using var scratch = new Scratch();
        var zip = Zip(scratch, "bundle.zip", "a.txt", "b.txt");

        var tarGz = await Env.RunOne("tar.gz", zip);
        Assert.Equal(scratch.PathOf("bundle.tar.gz"), tarGz);
        File.Delete(zip);

        var folder = await Env.RunOne("extract", tarGz);
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task With_7zip_a_zip_repacks_as_7z()
    {
        Assert.SkipWhen(Env.Tools.SevenZip is null, "7-Zip isn't installed");
        using var scratch = new Scratch();
        var zip = Zip(scratch, "bundle.zip", "a.txt", "b.txt");

        var sevenZip = await Env.RunOne("7z", zip);
        File.Delete(zip);

        var folder = await Env.RunOne("extract", sevenZip);
        Assert.Equal(["a.txt", "b.txt"], Directory.GetFiles(folder).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task With_7zip_files_compress_into_a_7z()
    {
        Assert.SkipWhen(Env.Tools.SevenZip is null, "7-Zip isn't installed");
        using var scratch = new Scratch();
        var output = await Env.RunOne("compress-7z", scratch.Text("a.txt", "x"), scratch.Text("b.txt", "y"));
        Assert.Equal(scratch.PathOf("Archive.7z"), output);
    }

    [Fact]
    public async Task A_gz_file_extracts_to_the_file_inside()
    {
        using var scratch = new Scratch();
        var gz = scratch.PathOf("notes.txt.gz");
        using (var stream = new GZipStream(File.Create(gz), CompressionLevel.Optimal))
            stream.Write("compressed text"u8);

        var output = await Env.RunOne("extract", gz);

        Assert.Equal(scratch.PathOf("notes.txt"), output);
        Assert.Equal("compressed text", File.ReadAllText(output));
    }

    static string Zip(Scratch scratch, string name, params string[] files)
    {
        var source = scratch.Folder(Path.GetFileNameWithoutExtension(name) + "-source", files);
        var zip = scratch.PathOf(name);
        ZipFile.CreateFromDirectory(source, zip);
        Directory.Delete(source, recursive: true);
        return zip;
    }
}
