using Tanj;

namespace Tanjelo.Core.Tests;

/// <summary><c>tanj --clipboard</c>: the switch taken out of the arguments, and a picture worked on in a scratch folder.</summary>
public class TanjClipboardTests
{
    [Fact]
    public void The_switch_is_taken_out_before_the_settings_are_read()
    {
        var (rest, fromClipboard) = ClipboardRun.Take(["convert", "compress", "--clipboard", "--mode=size", "--size=2"]);
        Assert.Equal(["convert", "compress", "--mode=size", "--size=2"], rest);
        Assert.True(fromClipboard);

        (rest, fromClipboard) = ClipboardRun.Take(["targets", "--clipboard"]);
        Assert.Equal(["targets"], rest);
        Assert.True(fromClipboard);

        (rest, fromClipboard) = ClipboardRun.Take(["convert", "jpg", "photo.png"]);
        Assert.Equal(["convert", "jpg", "photo.png"], rest);
        Assert.False(fromClipboard);
    }

    [Fact]
    public void Copied_files_are_the_input_and_a_picture_is_saved_in_a_scratch_folder()
    {
        using var scratch = new Scratch();
        var copied = scratch.Text("notes.txt", "x");
        var clipboard = new FakeClipboard { Data = new ClipboardData { Files = [copied] } };
        using (var input = new ClipboardRun(clipboard))
        {
            Assert.Equal([copied], input.Read(out _));
            Assert.Null(input.Picture);
            Assert.Null(input.Scratch);
        }

        clipboard.Data = new ClipboardData { Png = File.ReadAllBytes(scratch.Image("shot.png", 30, 20)) };
        string folder;
        using (var input = new ClipboardRun(clipboard))
        {
            var file = Assert.Single(input.Read(out _)!);
            folder = input.Scratch!;
            Assert.Equal(folder, Path.GetDirectoryName(file));
            Assert.StartsWith("Screenshot ", Path.GetFileName(file));
            Assert.Equal(clipboard.Data.Png, File.ReadAllBytes(file));
            Assert.Equal((30, 20), (input.Picture!.Width, input.Picture.Height));
            Assert.Equal("clipboard picture: PNG 30 × 20", string.Format(Strings.ClipboardPictureNote, input.Picture.Width, input.Picture.Height));
        }
        Assert.False(Directory.Exists(folder));   // gone when tanj ends

        clipboard.Data = ClipboardData.Empty;
        using (var input = new ClipboardRun(clipboard))
        {
            Assert.Null(input.Read(out var problem));
            Assert.Equal("No picture or files on the clipboard.", problem);
        }
    }

    [Fact]
    public void Results_written_in_the_scratch_folder_move_into_the_current_one()
    {
        using var scratch = new Scratch();
        var work = scratch.Folder("work", "Screenshot.jpg", "Screenshot.png");
        var pages = Directory.CreateDirectory(Path.Combine(work, "Screenshot pages")).FullName;
        File.WriteAllText(Path.Combine(pages, "1.png"), "1");
        File.WriteAllText(Path.Combine(pages, "2.png"), "2");
        var here = scratch.Folder("here", "Screenshot.jpg");
        var saved = scratch.Text("saved.png", "elsewhere");

        var moved = ClipboardRun.MoveOut(
            [Path.Combine(work, "Screenshot.jpg"), Path.Combine(pages, "1.png"), Path.Combine(pages, "2.png"), saved], work, here);

        Assert.Equal([
            Path.Combine(here, "Screenshot (2).jpg"),   // the name was taken
            Path.Combine(here, "Screenshot pages", "1.png"),
            Path.Combine(here, "Screenshot pages", "2.png"),
            saved,                                       // written elsewhere, as Save to… does
        ], moved);
        Assert.Equal("contents of Screenshot.jpg", File.ReadAllText(Path.Combine(here, "Screenshot.jpg")));
        Assert.Equal("2", File.ReadAllText(Path.Combine(here, "Screenshot pages", "2.png")));
        Assert.Equal(["Screenshot.png"], Directory.GetFileSystemEntries(work).Select(Path.GetFileName));   // the picture itself stays behind
    }

    [Fact]
    public async Task A_screenshot_on_the_clipboard_converts_into_the_current_folder()
    {
        using var scratch = new Scratch();
        var here = scratch.Folder("here");
        var clipboard = new FakeClipboard { Data = new ClipboardData { Png = File.ReadAllBytes(scratch.Image("shot.png", 40, 30)) } };
        using var input = new ClipboardRun(clipboard);
        var files = input.Read(out _)!;
        var engine = Env.Engine();
        var target = ClipboardPicture.ForMenu(engine.GetMenu(files), null).Targets.Single(t => t.Key == "jpg");

        var result = await engine.RunAsync(files, target, ct: TestContext.Current.CancellationToken);
        var moved = ClipboardRun.MoveOut(result.Outputs, input.Scratch!, here);

        var jpg = Assert.Single(moved);
        Assert.Equal(here, Path.GetDirectoryName(jpg));
        Assert.EndsWith(".jpg", jpg);
        Assert.Equal((40, 30), ((int)Inspect.Image(jpg).Width, (int)Inspect.Image(jpg).Height));
        Assert.Null(clipboard.Picture);   // tanj leaves the clipboard as it is
        Assert.Null(clipboard.Files);
    }
}
