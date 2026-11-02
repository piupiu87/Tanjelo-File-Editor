using System.Windows;
using System.Windows.Automation;
using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

/// <summary>
/// The menu for a picture on the clipboard, what the shortcut and the tray open with nothing selected, and runs from a
/// fake clipboard into a scratch clipboard folder.
/// </summary>
public class ClipboardMenuTests
{
    static readonly string[] Left = ["copy-path", "copy-files", "open-terminal", "move-to", "metadata"];

    [Fact]
    public void The_clipboard_picture_s_menu_has_every_picture_entry_and_none_for_the_user_s_own_files()
    {
        using var scratch = new Scratch();
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => ClipboardFolderTests.Taken);
        var (file, _) = folder.Save(ClipboardFolderTests.Picture(scratch, "shot.png", System.Drawing.Color.SteelBlue), 1);
        var engine = Env.Engine();
        var full = engine.GetMenu([file]);

        var menu = ClipboardPicture.ForMenu(full, saveHere: null);
        var keys = menu.Targets.Select(t => t.Key).ToList();

        Assert.Equal(full.Targets.Where(t => t.Section != Section.Archive).Select(t => t.Key).Except(Left), keys);   // the rest, in the same order
        Assert.Contains(full.Targets, t => t.Section == Section.Archive);
        Assert.DoesNotContain(menu.Targets, t => t.Section == Section.Archive);   // no zipping a screenshot
        foreach (var key in new[] { "jpg", "webp", "avif", "gif", "bmp", "tiff", "ico", "pdf", "compress", "crop", "background", "redact", "annotate", "adjust", "share", "float", "save-to" })
            Assert.Contains(key, keys);
        Assert.DoesNotContain("png", keys);   // it's a PNG already
        Assert.Equal(Env.Tools.Ocr, keys.Contains("txt"));
        Assert.Equal(Env.Tools.Heic, keys.Contains("heic"));
        Assert.All(Left, key => Assert.DoesNotContain(key, keys));
        Assert.Single(menu.Targets, t => t.Key == "save-to");   // no Save in "Folder" without a folder
    }

    [Fact]
    public void Save_in_the_folder_in_front_follows_Save_to()
    {
        using var scratch = new Scratch();
        var file = scratch.Image("shot.png");
        var desktop = scratch.Folder("Desktop");

        var targets = ClipboardPicture.ForMenu(Env.Engine().GetMenu([file]), desktop).Targets.ToList();

        var saveTo = targets.FindIndex(t => t.Key == "save-to");
        var saveHere = targets[saveTo + 1];
        Assert.Equal("Save in \"Desktop\"", saveHere.Label);
        Assert.Equal(desktop, saveHere.Parameters["folder"]);
        Assert.Null(saveHere.FolderPrompt);
        Assert.NotNull(targets[saveTo].FolderPrompt);
    }

    [Fact]
    public void With_nothing_selected_copied_files_come_first_then_the_picture()
    {
        using var scratch = new Scratch();
        var selected = scratch.Text("selected.txt", "x");
        var copied = scratch.Text("copied.txt", "x");
        var png = File.ReadAllBytes(scratch.Image("shot.png", 30, 20));
        var desktop = scratch.Folder("Desktop");
        var both = new ClipboardData { Files = [copied], Png = png };

        // A selection wins, and the clipboard isn't even read.
        string[] selection = [selected];
        var chosen = ClipboardInput.Choose(selection, desktop, null);
        Assert.Same(selection, chosen.Files);
        Assert.Equal((null, null), (chosen.Picture, chosen.SaveHere));

        var files = ClipboardInput.Choose([], desktop, both);
        Assert.Equal([copied], files.Files);
        Assert.Null(files.SaveHere);   // copied files' results go beside them

        var picture = ClipboardInput.Choose([], desktop, new ClipboardData { Png = png });
        Assert.Equal((30, 20), (picture.Picture!.Width, picture.Picture.Height));
        Assert.Equal(desktop, picture.SaveHere);
        Assert.Null(ClipboardInput.Choose([], null, new ClipboardData { Png = png }).SaveHere);   // from another app, or the tray

        var nothing = ClipboardInput.Choose([], desktop, ClipboardData.Empty);
        Assert.True(nothing.IsEmpty);
        Assert.Null(nothing.Problem);
        var tooLarge = ClipboardInput.Choose([], desktop, new ClipboardData { Bitmap = ClipboardPictureTests.Dib(40, 20_000, 15_000, 32, 0, []) });
        Assert.True(tooLarge.IsEmpty);
        Assert.Contains("250 megapixels", tooLarge.Problem);
    }

    [Fact]
    public async Task A_screenshot_from_the_clipboard_is_converted_and_edited_beside_itself_in_Tanjelo_s_folder()
    {
        using var scratch = new Scratch();
        // As Snipping Tool copies it: a DIBV5 of the screen, here with some text on it.
        var (pixels, width, height) = ClipboardPicture.Pixels(scratch.TextPicture("screen.png", 700, 300, "Meeting moved to Thursday"));
        uint[] masks = [0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000];
        var clipboard = new FakeClipboard { Data = new ClipboardData { Bitmap = ClipboardPictureTests.Dib(124, width, -height, 32, 3, pixels, headerMasks: masks) } };
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => ClipboardFolderTests.Taken);
        var shelf = new Shelf(folder);
        var (shot, _) = folder.Save(ClipboardPicture.Choose(clipboard.Read()).Picture!, clipboard.Sequence);
        var engine = Env.Engine();
        var keys = new List<(string Key, Dictionary<string, string> Values)>
        {
            ("jpg", new()),
            ("annotate", new() { ["spec"] = MarkSpec.Write([new Mark { Kind = MarkKind.Rectangle, Colour = "#FF0000", Points = [new(10, 10), new(200, 80)] }]) }),
            ("crop", new() { ["aspect"] = "1:1" }),
        };
        if (Env.Tools.Ocr) keys.Add(("txt", new()));

        foreach (var (key, values) in keys)
        {
            var result = await engine.RunAsync([shot], Env.Offered(engine, key, [shot]).WithParameters(values), ct: TestContext.Current.CancellationToken);
            Assert.True(result.Failures.Count == 0, $"{key}: " + string.Join("; ", result.Failures.Select(f => f.Message)));
            var output = Assert.Single(result.Outputs);
            Assert.Equal(folder.Path, Path.GetDirectoryName(output));
            Assert.StartsWith("Screenshot 2026-11-02 143005", Path.GetFileName(output));
            Assert.True(shelf.Holds(output), key);
        }
        if (Env.Tools.Ocr)
            Assert.Contains("Meeting moved to Thursday", File.ReadAllText(Path.Combine(folder.Path, "Screenshot 2026-11-02 143005.txt")));

        // Save to… reaches the user's own folder, as a copy that isn't held.
        var pictures = scratch.Folder("Pictures");
        var saved = await engine.RunAsync([shot], Env.Offered(engine, "save-to", [shot]).WithParameters(new Dictionary<string, string> { ["folder"] = pictures }),
            ct: TestContext.Current.CancellationToken);
        var copy = Assert.Single(saved.Outputs);
        Assert.Equal(Path.Combine(pictures, "Screenshot 2026-11-02 143005.png"), copy);
        Assert.False(shelf.Holds(copy));
        Assert.True(File.Exists(shot));
    }

    [Fact]
    public async Task The_menu_for_a_clipboard_picture_says_what_it_is_and_shows_it()
    {
        using var scratch = new Scratch();
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => ClipboardFolderTests.Taken);
        var picture = ClipboardFolderTests.Picture(scratch, "shot.png", System.Drawing.Color.SteelBlue, 1920, 1080);
        var (file, _) = folder.Save(picture, 1);
        await Ui.Run(async () =>
        {
            var model = ClipboardPicture.ForMenu(Env.Engine().GetMenu([file]), scratch.Folder("Desktop"));
            var menu = new FormatMenuWindow([file], model, null, (Tanjelo.Strings.ClipboardPicture, string.Format(Tanjelo.Strings.PngSize, 1920, 1080)));
            var host = Ui.Host(menu, 330, 700);
            using var attached = A11y.Attach(host);
            await Ui.Settle();

            Assert.Equal("Clipboard picture", menu.Heading.Text);
            Assert.Equal("PNG · 1920 × 1080", menu.Subheading.Text);
            Assert.NotNull(menu.Thumbnail.Source);   // the picture itself
            A11y.Check(host, "clipboard menu", c => c is System.Windows.Controls.RadioButton || c is System.Windows.Controls.ListBoxItem);
            Ui.Render(host, "menu-clipboard-light");
            Ui.UseTheme("dark", host);
            Ui.Render(host, "menu-clipboard-dark");
            Ui.UseTheme("light", host);
            menu.Press(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.None);
            menu.Press(System.Windows.Input.Key.Tab, System.Windows.Input.ModifierKeys.None);
            Assert.True(menu.ActionsTab.IsChecked);
            Assert.Contains(menu.List.Items.Cast<FormatMenuWindow.Entry>(), e => e.Label == "Save in \"Desktop\"");
            AccessibilityTests.RenderInContrast(host, "menu-clipboard-contrast", Theme.Contrast.Aquatic);
        });
    }

    [Fact]
    public async Task Results_on_the_shelf_offer_to_save_a_copy_rather_than_show_Tanjelo_s_folder()
    {
        using var scratch = new Scratch();
        var shot = scratch.Image("Screenshot.png");
        var annotated = scratch.Image("Screenshot-annotated.png");
        var saved = new List<IReadOnlyList<string>>();
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "jpg", [shot]) with { Done = "Annotated" };
            var card = new ProgressToast { SaveCopies = paths => { saved.Add(paths); return paths; } };
            card.ShowResult(target, [shot], new BatchResult([annotated], [], [], false), onShelf: true, copied: true);
            var host = Ui.Host(card, 360, 160);
            using var attached = A11y.Attach(host);
            await Ui.Settle();

            // Put back on the clipboard already, so there's no Copy.
            Assert.Equal("Annotated", card.Heading.Text);
            Assert.Equal("On the shelf · Copied to the clipboard", card.Detail.Text);
            Assert.Equal(Visibility.Collapsed, card.CopyButton.Visibility);
            Assert.Equal((Tanjelo.Strings.Open, Tanjelo.Strings.SaveAs), ((string)card.SecondaryButton.Content, (string)card.PrimaryButton.Content));
            Assert.Equal("Annotated. On the shelf · Copied to the clipboard", AutomationProperties.GetName(card.Card));
            A11y.InOrder(A11y.Check(host, "card on the shelf"), card.SecondaryButton, card.PrimaryButton, card.CloseButton);
            Ui.Render(host, "card-shelf-light");
            AccessibilityTests.RenderInContrast(host, "card-shelf-contrast", Theme.Contrast.Desert);

            card.PrimaryButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal([[annotated]], saved.Select(s => s.ToArray()));
        });
    }

    [Fact]
    public async Task Every_result_card_that_wrote_files_has_Copy()
    {
        using var scratch = new Scratch();
        var photo = scratch.Image("photo.png");
        var output = scratch.Image("photo.jpg");
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "jpg", [photo]);
            var clipboard = new FakeClipboard();
            var card = new ProgressToast { Clipboard = clipboard };
            card.ShowResult(target, [photo], new BatchResult([output], [], [], false));
            var host = Ui.Host(card, 360, 160);
            using var attached = A11y.Attach(host);
            await Ui.Settle();

            Assert.Equal((Tanjelo.Strings.Copy, Tanjelo.Strings.Open, Tanjelo.Strings.ShowInFolder),
                ((string)card.CopyButton.Content, (string)card.SecondaryButton.Content, (string)card.PrimaryButton.Content));
            A11y.InOrder(A11y.Check(host, "card with Copy"), card.CopyButton, card.SecondaryButton, card.PrimaryButton, card.CloseButton);
            Ui.Render(host, "card-copy-light");
            Ui.UseTheme("dark", host);
            Ui.Render(host, "card-copy-dark");
            Ui.UseTheme("light", host);

            card.CopyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(output, clipboard.Picture);   // a picture goes on as a picture
            Assert.Equal("photo.jpg · Copied to the clipboard", card.Detail.Text);
            Assert.Equal(Visibility.Collapsed, card.CopyButton.Visibility);

            // Results on the shelf that weren't put back on the clipboard, with the setting off, can be copied too.
            var held = new ProgressToast { Clipboard = clipboard };
            held.ShowResult(target, [photo], new BatchResult([output], [], [], false), onShelf: true);
            Assert.Equal(("On the shelf", Visibility.Visible), (held.Detail.Text, held.CopyButton.Visibility));
            // A card that wrote nothing has none.
            var nothing = new ProgressToast { Clipboard = clipboard };
            nothing.ShowResult(Env.Offered(Env.Engine(), "copy-path", [photo]), [photo], new BatchResult([], [], [], false));
            Assert.Equal(Visibility.Collapsed, nothing.CopyButton.Visibility);
        });
    }
}
