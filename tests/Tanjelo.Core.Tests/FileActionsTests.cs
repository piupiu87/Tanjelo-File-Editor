using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class FileActionsTests
{
    /// <summary>Records what would have gone on the clipboard, so the tests leave the real one alone.</summary>
    sealed class FakeClipboard : IClipboard
    {
        public string? Text { get; private set; }
        public IReadOnlyList<string>? Files { get; private set; }
        public void SetText(string text) => Text = text;
        public void SetFiles(IReadOnlyList<string> paths) => Files = paths;
    }

    readonly FakeClipboard _clipboard = new();

    ConverterEngine Engine() => new(Env.Tools, null, [], [new ClipboardActions(_clipboard)]);

    [Fact]
    public void Actions_are_offered_for_files_folders_and_mixed_selections()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("notes.txt", "x");
        var folder = scratch.Folder("photos");
        string[] expected = ["copy-path", "copy-files"];

        foreach (var selection in new[] { [file], [folder], new[] { file, folder } })
        {
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

}
