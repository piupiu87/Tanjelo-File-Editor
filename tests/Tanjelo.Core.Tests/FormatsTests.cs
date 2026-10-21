namespace Tanjelo.Core.Tests;

public class FormatsTests
{
    [Theory]
    [InlineData("photo.PNG", "png")]
    [InlineData("backup.tar.gz", "tar.gz")]
    [InlineData("backup.TGZ", "tar.gz")]
    [InlineData("logs.tar.xz", "tar.xz")]
    [InlineData("notes.gz", "gz")]
    [InlineData("README", "")]
    public void Ext_reads_compound_extensions(string name, string expected) => Assert.Equal(expected, Formats.Ext(name));

    [Theory]
    [InlineData("photo.jpeg", "jpg")]
    [InlineData("photo.jfif", "jpg")]
    [InlineData("scan.tif", "tiff")]
    [InlineData("clip.mpeg", "mpg")]
    [InlineData("song.aif", "aiff")]
    public void Key_maps_aliases_to_one_format(string name, string expected) => Assert.Equal(expected, Formats.Key(name));

    [Theory]
    [InlineData(@"C:\x\backup.tar.gz", "backup")]
    [InlineData(@"C:\x\my.photo.png", "my.photo")]
    [InlineData(@"C:\x\report", "report")]
    public void BaseName_drops_the_whole_extension(string path, string expected) => Assert.Equal(expected, Formats.BaseName(path));

    [Theory]
    [InlineData("a.heic", FileKind.Image)]
    [InlineData("a.mkv", FileKind.Video)]
    [InlineData("a.opus", FileKind.Audio)]
    [InlineData("a.pdf", FileKind.Pdf)]
    [InlineData("a.rtf", FileKind.WordDocument)]
    [InlineData("a.odp", FileKind.Presentation)]
    [InlineData("a.csv", FileKind.Spreadsheet)]
    [InlineData("a.7z", FileKind.Archive)]
    [InlineData("a.xyz", FileKind.Unknown)]
    public void KindOf_classifies_by_extension(string name, FileKind expected) => Assert.Equal(expected, Formats.KindOf(name));

    [Fact]
    public void KindOf_recognises_folders()
    {
        using var scratch = new Scratch();
        Assert.Equal(FileKind.Folder, Formats.KindOf(scratch.Folder("photos.png")));
    }
}

public class OutputPathsTests
{
    [Fact]
    public void Unique_never_reuses_an_existing_name()
    {
        using var scratch = new Scratch();
        var input = scratch.Text("notes.txt", "x");

        Assert.Equal(scratch.PathOf("notes.pdf"), OutputPaths.Beside(input, "pdf"));
        Assert.Equal(scratch.PathOf("notes (2).txt"), OutputPaths.Beside(input, "txt"));
        File.WriteAllText(scratch.PathOf("notes (2).txt"), "");
        Assert.Equal(scratch.PathOf("notes (3).txt"), OutputPaths.Beside(input, "txt"));
    }

    [Fact]
    public void Unique_treats_folders_as_taken()
    {
        using var scratch = new Scratch();
        scratch.Folder("Archive");
        Assert.Equal(scratch.PathOf("Archive (2)"), OutputPaths.Unique(scratch.Dir, "Archive", ""));
    }
}
