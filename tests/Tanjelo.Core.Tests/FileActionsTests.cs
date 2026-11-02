using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class FileActionsTests
{
    readonly FakeClipboard _clipboard = new();

    ConverterEngine Engine() => new(Env.Tools, null, [new SaveToFolderAction(), new MoveToFolderAction()], [new ClipboardActions(_clipboard), new OpenTerminalAction(Env.Tools)]);

    [Fact]
    public void Actions_are_offered_for_files_folders_and_mixed_selections()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("notes.txt", "x");
        var folder = scratch.Folder("photos");
        foreach (var selection in new[] { [file], [folder], new[] { file, folder } })
        {
            // The Share sheet takes files only.
            string[] expected = selection is [var only] && only == file
                ? ["copy-path", "copy-files", "open-terminal", "share", "float", "save-to", "move-to"]
                : ["copy-path", "copy-files", "open-terminal", "float", "save-to", "move-to"];
            var actions = Env.Engine().GetMenu(selection).Targets.Where(t => t.Kind == TargetKind.Action).ToList();
            Assert.Equal(expected, actions.Select(t => t.Key));
            Assert.All(actions, a => Assert.Equal(Section.File, a.Section));
        }
    }

    [Fact]
    public async Task Copy_path_puts_one_path_per_line_on_the_clipboard()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("notes.txt", "x");
        var folder = scratch.Folder("photos");
        var engine = Engine();

        var result = await engine.RunAsync([file, folder + "\\"], Env.Offered(engine, "copy-path", [file, folder]), ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Failures);
        Assert.Empty(result.Outputs);
        Assert.Equal(file + "\r\n" + folder, _clipboard.Text);
    }

    [Fact]
    public async Task Copy_path_of_a_drive_keeps_its_backslash()
    {
        var engine = Engine();
        await engine.RunAsync([@"C:\"], Env.Offered(engine, "copy-path", [@"C:\"]), ct: TestContext.Current.CancellationToken);
        Assert.Equal(@"C:\", _clipboard.Text);
    }

    [Fact]
    public async Task Copy_puts_the_files_on_the_clipboard_for_pasting()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "x");
        var b = scratch.Folder("b");
        var engine = Engine();

        await engine.RunAsync([a, b], Env.Offered(engine, "copy-files", [a, b]), ct: TestContext.Current.CancellationToken);

        Assert.Equal([a, b], _clipboard.Files);
    }

    [Fact]
    public void Results_go_on_the_clipboard_as_a_picture_as_text_or_as_files()
    {
        using var scratch = new Scratch();
        var png = scratch.Image("shot-annotated.png");
        var jpg = scratch.Image("shot.jpg");
        var text = scratch.Text("shot.txt", "Meeting moved to Thursday");
        var long_ = scratch.Text("long.txt", new string('x', (int)ClipboardResults.MaxText + 1));
        var pdf = scratch.Text("shot.pdf", "%PDF");
        var folder = scratch.Folder("pages", "1.png");

        ClipboardResults.Copy(_clipboard, [png]);
        Assert.Equal(png, _clipboard.Picture);
        ClipboardResults.Copy(_clipboard, [jpg]);
        Assert.Equal(jpg, _clipboard.Picture);
        ClipboardResults.Copy(_clipboard, [text]);
        Assert.Equal("Meeting moved to Thursday", _clipboard.Text);
        foreach (var files in new[] { [long_], [pdf], [folder], new[] { png, jpg } })
        {
            ClipboardResults.Copy(_clipboard, files);
            Assert.Equal(files, _clipboard.Files);
            Assert.Null(_clipboard.Picture);
        }
        Assert.True(ClipboardResults.IsPicture(png));
        Assert.False(ClipboardResults.IsPicture(scratch.PathOf("gone.png")));
        Assert.False(ClipboardResults.IsPicture(pdf));
    }

    [Fact]
    public void Terminal_opens_in_each_folder_once_and_in_the_folder_of_each_file()
    {
        using var scratch = new Scratch();
        var sub = scratch.Folder("sub");
        var folders = OpenTerminalAction.Folders([scratch.Text("a.txt", ""), scratch.Text("b.txt", ""), sub, sub + "\\"]);
        Assert.Equal([scratch.Dir, sub], folders);
    }

    [Fact]
    public void Windows_terminal_gets_one_new_window_with_a_tab_per_folder()
    {
        var start = Assert.Single(OpenTerminalAction.StartInfos([@"C:\one", @"D:\two;three"], @"C:\wt.exe"));

        Assert.Equal(@"C:\wt.exe", start.FileName);
        Assert.Equal(["-w", "new", "-d", @"C:\one", ";", "new-tab", "-d", @"D:\two\;three"], start.ArgumentList);
    }

    [Fact]
    public void Without_windows_terminal_each_folder_gets_a_powershell_window()
    {
        var starts = OpenTerminalAction.StartInfos([@"C:\one", @"C:\two"], windowsTerminal: null);

        Assert.All(starts, s => Assert.Equal("powershell.exe", s.FileName));
        Assert.All(starts, s => Assert.True(s.UseShellExecute));
        Assert.Equal([@"C:\one", @"C:\two"], starts.Select(s => s.WorkingDirectory));
    }

    [Fact]
    public async Task Move_puts_files_and_folders_in_the_chosen_folder()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("Photo.JPG", "picture");
        var folder = scratch.Folder("album", "1.txt");
        var destination = scratch.Folder("archive");

        var result = await Move(destination, file, folder);

        Assert.Equal([Path.Combine(destination, "Photo.JPG"), Path.Combine(destination, "album")], result.Outputs);
        Assert.False(File.Exists(file));
        Assert.False(Directory.Exists(folder));
        Assert.Equal("contents of 1.txt", File.ReadAllText(Path.Combine(destination, "album", "1.txt")));
    }

    [Fact]
    public async Task Move_never_overwrites_and_keeps_the_extension_as_written()
    {
        using var scratch = new Scratch();
        var destination = scratch.Folder("archive", "Photo.JPG");
        var file = scratch.Text("Photo.JPG", "new picture");

        var result = await Move(destination, file);

        Assert.Equal([Path.Combine(destination, "Photo (2).JPG")], result.Outputs);
        Assert.Equal("contents of Photo.JPG", File.ReadAllText(Path.Combine(destination, "Photo.JPG")));
    }

    [Fact]
    public async Task One_item_that_cant_move_doesnt_stop_the_others()
    {
        using var scratch = new Scratch();
        var parent = scratch.Folder("parent");
        var child = Directory.CreateDirectory(Path.Combine(parent, "child")).FullName;
        var file = scratch.Text("notes.txt", "x");

        var result = await Move(child, parent, file);

        Assert.Equal([Path.Combine(child, "notes.txt")], result.Outputs);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(parent, failure.Input);
        Assert.Equal("A folder can't be moved into itself.", failure.Message);
    }

    [Theory]
    [InlineData("", "Choose a folder to move to.")]
    [InlineData("missing", "That folder doesn't exist.")]
    [InlineData(".", "It's already in that folder.")]
    public async Task Move_explains_what_went_wrong(string folder, string message)
    {
        using var scratch = new Scratch();
        var file = scratch.Text("notes.txt", "x");

        var result = await Move(folder.Length == 0 ? "" : Path.Combine(scratch.Dir, folder), file);

        Assert.Equal(message, Assert.Single(result.Failures).Message);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Moving_a_folder_to_another_drive_copies_it_then_removes_the_original()
    {
        using var scratch = new Scratch();
        var folder = scratch.Folder("album", "1.txt", "2.txt");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "nested", "3.txt"), "deep");
        var output = scratch.PathOf("moved");

        MoveToFolderAction.MoveAcrossDrives(folder, output, CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(output, "nested", "3.txt")));
        Assert.Equal(2, Directory.GetFiles(output).Length);
    }

    [Fact]
    public async Task Save_to_copies_files_and_folders_into_the_chosen_folder_and_leaves_them_where_they_are()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("Shot.PNG", "picture");
        var folder = scratch.Folder("album", "1.txt");
        var destination = scratch.Folder("saved", "Shot.PNG");

        var result = await Save(destination, file, folder);

        Assert.Empty(result.Failures);
        Assert.Equal([Path.Combine(destination, "Shot (2).PNG"), Path.Combine(destination, "album")], result.Outputs);
        Assert.Equal("picture", File.ReadAllText(Path.Combine(destination, "Shot (2).PNG")));
        Assert.Equal("contents of Shot.PNG", File.ReadAllText(Path.Combine(destination, "Shot.PNG")));   // never overwritten
        Assert.Equal("contents of 1.txt", File.ReadAllText(Path.Combine(destination, "album", "1.txt")));
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(Path.Combine(folder, "1.txt")));
    }

    [Theory]
    [InlineData("", "Choose a folder to save to.")]
    [InlineData("missing", "That folder doesn't exist.")]
    [InlineData(".", "It's already in that folder.")]
    [InlineData(@"album\inner", "A folder can't be copied into itself.")]
    public async Task Save_to_explains_what_went_wrong(string folder, string message)
    {
        using var scratch = new Scratch();
        var album = scratch.Folder("album");
        Directory.CreateDirectory(Path.Combine(album, "inner"));
        var input = folder.StartsWith("album") ? album : scratch.Text("notes.txt", "x");
        var before = scratch.Files();

        var result = await Save(folder.Length == 0 ? "" : Path.Combine(scratch.Dir, folder), input);

        Assert.Equal(message, Assert.Single(result.Failures).Message);
        Assert.Equal(before, scratch.Files());   // nothing copied
        Assert.Equal(["inner"], Directory.GetDirectories(album).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Save_in_a_folder_already_chosen_copies_there_without_asking()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("shot.png", "picture");
        var here = scratch.Folder("Desktop");
        var engine = Engine();

        var target = SaveToFolderAction.In(Env.Offered(engine, "save-to", [file]), here);
        var result = await engine.RunAsync([file], target, ct: TestContext.Current.CancellationToken);

        Assert.Equal("Save in \"Desktop\"", target.Label);
        Assert.Equal(here, target.Description);
        Assert.Null(target.FolderPrompt);   // the app asks for no folder
        Assert.Equal([Path.Combine(here, "shot.png")], result.Outputs);
        Assert.True(File.Exists(file));
    }

    async Task<BatchResult> Save(string destination, params string[] inputs)
    {
        var engine = Engine();
        var target = Env.Offered(engine, "save-to", inputs).WithParameters(new Dictionary<string, string> { ["folder"] = destination });
        return await engine.RunAsync(inputs, target, ct: TestContext.Current.CancellationToken);
    }

    async Task<BatchResult> Move(string destination, params string[] inputs)
    {
        var engine = Engine();
        var target = Env.Offered(engine, "move-to", inputs).WithParameters(new Dictionary<string, string> { ["folder"] = destination });
        return await engine.RunAsync(inputs, target, ct: TestContext.Current.CancellationToken);
    }
}
