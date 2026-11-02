namespace Tanjelo.Core.Tests;

/// <summary>Which changes to the clipboard are screenshots, told what happened and when, without Windows' clipboard.</summary>
public class ScreenshotWatcherTests
{
    static readonly DateTime Start = new(2026, 11, 2, 14, 30, 5);

    static readonly string[] SnippingToolFormats = ["PNG", "CF_DIBV5", "CF_DIB", "CF_BITMAP"];

    static ClipboardChange Change(string? owner, double seconds, params string[] formats) =>
        new(owner, formats.Length == 0 ? SnippingToolFormats : formats, (uint)(100 + seconds * 10), Start.AddSeconds(seconds));

    [Theory]
    [InlineData("SnippingTool.exe")]
    [InlineData("ScreenClippingHost.exe")]
    [InlineData("ScreenSketch.exe")]
    [InlineData("snippingtool.EXE")]
    public void Screenshot_tools_pictures_are_screenshots(string owner)
    {
        Assert.True(new ScreenshotWatcher().Notice(Change(owner, 0), out var skipped));
        Assert.Null(skipped);
    }

    [Fact]
    public void Print_Screen_s_bitmap_with_no_owner_is_a_screenshot()
    {
        Assert.True(new ScreenshotWatcher().Notice(Change(null, 0, "CF_BITMAP", "CF_DIB", "CF_DIBV5"), out _));
        Assert.False(new ScreenshotWatcher().Notice(Change(null, 0, "CF_UNICODETEXT", "CF_LOCALE"), out _));
        Assert.False(new ScreenshotWatcher().Notice(Change(null, 0, "CF_BITMAP", "CF_UNICODETEXT"), out _));
        Assert.False(new ScreenshotWatcher().Notice(new ClipboardChange(null, [], 1, Start), out _));   // emptied
    }

    [Fact]
    public void A_picture_from_any_other_app_is_left_alone_and_logged()
    {
        var watcher = new ScreenshotWatcher();
        Assert.False(watcher.Notice(Change("chrome.exe", 0, "PNG", "CF_DIB", "HTML Format"), out var skipped));
        Assert.Contains("chrome.exe", skipped);
        Assert.False(watcher.Notice(Change("notepad.exe", 1, "CF_UNICODETEXT"), out skipped));
        Assert.Null(skipped);   // not a picture: nothing worth a line
        Assert.False(watcher.Notice(Change("SnippingTool.exe", 2, "CF_UNICODETEXT"), out _));   // text copied from Snipping Tool
        Assert.False(ScreenshotWatcher.MightBeScreenshot("chrome.exe"));
        Assert.True(ScreenshotWatcher.MightBeScreenshot(null));
    }

    [Fact]
    public void What_password_managers_ask_watchers_not_to_look_at_and_Tanjelo_s_own_copies_are_left_alone()
    {
        var watcher = new ScreenshotWatcher();
        Assert.False(watcher.Notice(Change("SnippingTool.exe", 0, ["PNG", "ExcludeClipboardContentFromMonitorProcessing"]), out var skipped));
        Assert.Null(skipped);
        Assert.False(watcher.Notice(Change("SnippingTool.exe", 1) with { HistoryAllowed = false }, out skipped));
        Assert.Null(skipped);
        Assert.False(watcher.Notice(Change("SnippingTool.exe", 2) with { FromTanjelo = true }, out _));
        Assert.Null(watcher.Due(Start.AddSeconds(10)));
    }

    [Fact]
    public void The_program_behind_a_process_and_the_names_of_formats_are_found()
    {
        // What the watcher asks about whoever copied something, asked here about this test run itself.
        Assert.Equal(Path.GetFileName(Environment.ProcessPath), Native.ProgramOf((uint)Environment.ProcessId));
        Assert.Equal("CF_DIBV5", Native.ClipboardFormatName(17));
        Assert.Equal("CF_BITMAP", Native.ClipboardFormatName(2));
        Assert.Equal("PNG", Native.ClipboardFormatName(RegisterClipboardFormat("PNG")));   // registered long ago by Windows
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern uint RegisterClipboardFormat(string name);

    [Fact]
    public void Turned_off_nothing_is_a_screenshot()
    {
        var watcher = new ScreenshotWatcher { Enabled = false };
        Assert.False(watcher.Notice(Change("SnippingTool.exe", 0), out _));
        Assert.Null(watcher.Due(Start.AddSeconds(5)));
    }

    [Fact]
    public void A_screenshot_is_read_once_the_clipboard_has_been_quiet_for_a_second()
    {
        var watcher = new ScreenshotWatcher();
        watcher.Notice(Change("SnippingTool.exe", 0), out _);
        watcher.Notice(Change("SnippingTool.exe", 0.3), out _);
        watcher.Notice(Change("SnippingTool.exe", 0.6), out _);

        Assert.Null(watcher.Due(Start.AddSeconds(1.2)));   // still settling
        var due = watcher.Due(Start.AddSeconds(1.7));
        Assert.Equal(Start.AddSeconds(0.6), due!.At);      // the last of the three, read once
        Assert.Null(watcher.Due(Start.AddSeconds(5)));

        // Something else copied meanwhile: the screenshot isn't on the clipboard any more.
        watcher.Notice(Change("SnippingTool.exe", 10), out _);
        watcher.Notice(Change("notepad.exe", 10.5, "CF_UNICODETEXT"), out _);
        Assert.Null(watcher.Due(Start.AddSeconds(20)));
    }

    [Fact]
    public void A_screenshot_copied_again_after_an_edit_takes_the_place_of_the_unused_one_before()
    {
        using var scratch = new Scratch();
        var folder = new ClipboardFolder(scratch.PathOf("Clipboard"), () => Start);
        var shelf = new Shelf(folder, _ => { });
        var watcher = new ScreenshotWatcher();
        var snip = Change("SnippingTool.exe", 0);
        var colours = new[] { System.Drawing.Color.Red, System.Drawing.Color.Green, System.Drawing.Color.Blue, System.Drawing.Color.Gold, System.Drawing.Color.Plum };

        // As the app parks them: saved, then in place of the one before or added.
        string Park(int n, ClipboardChange change, double seconds, int width = 40)
        {
            var picture = ClipboardFolderTests.Picture(scratch, $"{n}.png", colours[n], width, 30);
            var (file, _) = folder.Save(picture, (uint)n);
            var replaced = watcher.Replaces(change, file, picture.Width, picture.Height, Start.AddSeconds(seconds), p => shelf.Contains(p) && !shelf.IsUsed(p));
            if (replaced is null || !shelf.Replace(replaced, file)) shelf.Add([file]);
            return file;
        }

        var first = Park(0, snip, 0);
        var edited = Park(1, snip, 10);
        Assert.Equal([edited], shelf.Items);   // one item, replaced, not a flood

        shelf.MarkUsed([edited]);              // dragged out, say
        var again = Park(2, snip, 15);
        Assert.Equal([edited, again], shelf.Items);

        var later = Park(3, snip, 50);         // more than 30 seconds on: a new screenshot
        var smaller = Park(4, snip, 55, width: 20);   // cropped: another size
        Assert.Equal([edited, again, later, smaller], shelf.Items);
        Assert.DoesNotContain(first, shelf.Items);

        var other = new ScreenshotWatcher();
        Assert.Null(other.Replaces(snip, "a.png", 40, 30, Start, _ => true));   // the first
        Assert.Null(other.Replaces(Change("ScreenClippingHost.exe", 1), "b.png", 40, 30, Start.AddSeconds(1), _ => true));   // another tool
    }
}
