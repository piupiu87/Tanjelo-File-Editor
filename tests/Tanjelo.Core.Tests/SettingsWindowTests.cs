using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Tanjelo.Core.Tests;

/// <summary>The settings window off-screen: every setting shown, each change handed on at once, and hand edits shown.</summary>
public class SettingsWindowTests
{
    /// <summary>Start with Windows and "Send to" as Windows would keep them, without touching this PC's.</summary>
    sealed class FakeWindows : IWindowsSettings
    {
        public bool StartsWithWindows { get; set; }
        public bool InSendToMenu { get; set; }
        public bool CanUseWindows11Menu { get; set; }
        public bool InWindows11Menu { get; set; }
    }

    sealed class Harness
    {
        public readonly List<Settings> Applied = [];
        public readonly FakeWindows Windows = new();
        public readonly List<(string? Ffmpeg, string? SevenZip)> Looks = [];
        public Toolbox Tools = new();
        public SettingsWindow Window = null!;
        public FrameworkElement Host = null!;

        public Settings Last => Applied[^1];

        public void Open(Settings settings)
        {
            Window = new SettingsWindow(settings, Applied.Add, Windows, (ffmpeg, sevenZip) =>
            {
                lock (Looks) Looks.Add((ffmpeg, sevenZip));
                return Tools;
            });
            Host = Ui.Host(Window, 600, 1500);
        }
    }

    [Fact]
    public async Task It_shows_every_setting_and_hands_on_each_change_at_once()
    {
        using var scratch = new Scratch();
        var ffmpeg = scratch.PathOf("ffmpeg.exe");
        File.WriteAllText(ffmpeg, "");
        await Ui.Run(async () =>
        {
            var h = new Harness { Tools = new Toolbox { Ffmpeg = ffmpeg, Word = true, Excel = true, WebView2 = "130.0" } };
            h.Windows.InSendToMenu = true;
            h.Open(new Settings { FloatHotkey = "Ctrl+Shift+F", JpegQuality = 80, Theme = "dark" });
            await h.Window.FindToolsAsync();
            await Ui.Settle();
            var w = h.Window;

            Assert.Equal("Ctrl+Shift+Space", w.MenuShortcut.Text);
            Assert.Equal("Ctrl+Shift+F", w.ShelfShortcut.Text);
            Assert.True(w.ThemeDark.IsChecked);
            Assert.Equal(80, w.JpegSlider.Value);
            Assert.Equal("80", w.JpegValue.Text);
            Assert.Equal("300 DPI", w.DpiValue.Text);
            Assert.False(w.StartupCheck.IsChecked);
            Assert.True(w.SendToCheck.IsChecked);
            Assert.Equal("Found: " + ffmpeg, w.FfmpegStatus.Text);
            Assert.StartsWith("Not found.", w.SevenZipStatus.Text);
            var others = w.OtherTools.Items.Cast<SettingsWindow.ToolRow>().ToList();
            Assert.Equal("Found: Word, Excel", others.Single(t => t.Name == "Microsoft Office").Status);
            Assert.False(others.Single(t => t.Name == "pandoc").Found);
            Assert.True(others.Single(t => t.Name == "WebView2").Found);
            Assert.Empty(h.Applied); // showing the settings changes nothing

            Ui.Render(h.Host, "settings-light");
            Ui.UseTheme("dark", h.Host);
            await Ui.Settle();
            Ui.Render(h.Host, "settings-dark");

            // Each control hands on a changed copy straight away.
            w.ThemeLight.IsChecked = true;
            Assert.Equal("light", h.Last.Theme);
            w.JpegSlider.Value = 72;
            Assert.Equal(72, h.Last.JpegQuality);
            Assert.Equal("72", w.JpegValue.Text);
            w.DpiSlider.Value = 150;
            Assert.Equal(150, h.Last.PdfRenderDpi);
            Assert.Equal(72, h.Last.JpegQuality); // earlier changes are kept
            w.WebpSlider.Value = 60;
            w.HeicSlider.Value = 55;
            Assert.Equal((60, 55), (h.Last.WebpQuality, h.Last.HeicQuality));

            // Windows' own settings go straight to Windows.
            w.StartupCheck.IsChecked = true;
            w.StartupCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(h.Windows.StartsWithWindows);
            w.SendToCheck.IsChecked = false;
            w.SendToCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(h.Windows.InSendToMenu);

            // Without the Windows 11 menu's package (an unsigned build, or Windows 10) the setting isn't offered.
            Assert.Equal(Visibility.Collapsed, w.Windows11MenuRow.Visibility);
        });
    }

    [Fact]
    public async Task The_Windows_11_menu_entry_is_offered_when_the_package_is_there()
    {
        await Ui.Run(async () =>
        {
            var h = new Harness();
            h.Windows.CanUseWindows11Menu = true;
            h.Open(new Settings());
            await Ui.Settle();
            var w = h.Window;
            Assert.Equal(Visibility.Visible, w.Windows11MenuRow.Visibility);
            Assert.False(w.Windows11MenuCheck.IsChecked);
            w.Windows11MenuCheck.IsChecked = true;
            w.Windows11MenuCheck.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(h.Windows.InWindows11Menu);
            Assert.Empty(h.Applied); // kept by Windows, not in settings.json
        });
    }

    [Fact]
    public void A_long_selection_arrives_in_a_file_that_is_then_deleted()
    {
        using var scratch = new Scratch();
        var list = scratch.PathOf("selection.txt");
        File.WriteAllLines(list, [@"C:\a.png", "", @"D:\folder with spaces"]);
        Assert.Equal([@"C:\a.png", @"D:\folder with spaces", @"E:\b.pdf"], App.Selection(["--paths-file", list, "--autostart", @"E:\b.pdf"]));
        Assert.False(File.Exists(list));
        Assert.Equal([@"C:\x.txt"], App.Selection([@"C:\x.txt", "--paths-file", scratch.PathOf("gone.txt")]));
    }

    [Fact]
    public async Task Shortcuts_are_recorded_from_the_keys_pressed()
    {
        await Ui.Run(async () =>
        {
            var h = new Harness();
            h.Open(new Settings());
            await Ui.Settle();
            var w = h.Window;

            // Modifiers alone show as they're held, and change nothing.
            Assert.True(w.MenuShortcut.Press(Key.LeftCtrl, ModifierKeys.Control));
            Assert.Equal("Ctrl+", w.MenuShortcut.Text);
            Assert.Empty(h.Applied);

            // A key with Ctrl, Alt or Win is the new shortcut, written the way settings.json reads it.
            Assert.True(w.MenuShortcut.Press(Key.D1, ModifierKeys.Control | ModifierKeys.Alt));
            Assert.Equal("Ctrl+Alt+1", h.Last.Hotkey);
            Assert.True(Settings.TryParseHotkey(h.Last.Hotkey, out var modifiers, out var key));
            Assert.Equal((ModifierKeys.Control | ModifierKeys.Alt, Key.D1), (modifiers, key));

            // A plain key or Shift with a key would get in the way of typing.
            var count = h.Applied.Count;
            Assert.True(w.MenuShortcut.Press(Key.A, ModifierKeys.Shift));
            Assert.Equal(count, h.Applied.Count);
            Assert.Equal(Visibility.Visible, w.ShortcutProblem.Visibility);

            // Tab and Escape still move on and close; the menu's shortcut can't be removed.
            Assert.False(w.MenuShortcut.Press(Key.Tab, ModifierKeys.None));
            Assert.False(w.MenuShortcut.Press(Key.Tab, ModifierKeys.Shift));
            Assert.False(w.MenuShortcut.Press(Key.Escape, ModifierKeys.None));
            Assert.True(w.MenuShortcut.Press(Key.Back, ModifierKeys.None));
            Assert.Equal(count, h.Applied.Count);
            Assert.Equal("Ctrl+Alt+1", w.MenuShortcut.Text);

            // The shelf's shortcut is optional: set, refused when it's the menu's, and removed.
            Assert.False(w.ClearShelfShortcut.IsEnabled);
            w.ShelfShortcut.Press(Key.F, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal("Ctrl+Shift+F", h.Last.FloatHotkey);
            Assert.True(w.ClearShelfShortcut.IsEnabled);
            count = h.Applied.Count;
            w.ShelfShortcut.Press(Key.D1, ModifierKeys.Control | ModifierKeys.Alt);
            Assert.Equal(count, h.Applied.Count);
            Assert.Equal("Ctrl+Shift+F", w.ShelfShortcut.Text);
            Assert.Contains("menu's shortcut", w.ShortcutProblem.Text);
            w.ShelfShortcut.Press(Key.Delete, ModifierKeys.None);
            Assert.Null(h.Last.FloatHotkey);
            Assert.Equal("", w.ShelfShortcut.Text);
            Assert.False(w.ClearShelfShortcut.IsEnabled);
            w.ShelfShortcut.Press(Key.F12, ModifierKeys.Windows);
            Assert.Equal("Win+F12", h.Last.FloatHotkey);
            w.ClearShelfShortcut.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Null(h.Last.FloatHotkey);
        });
    }

    [Fact]
    public async Task Program_paths_are_checked_as_they_are_typed_and_hand_edits_show_up()
    {
        using var scratch = new Scratch();
        var sevenZip = scratch.PathOf("7z.exe");
        File.WriteAllText(sevenZip, "");
        await Ui.Run(async () =>
        {
            var h = new Harness();
            h.Open(new Settings());
            await h.Window.FindToolsAsync();
            var w = h.Window;

            // A path that isn't there says so; the box's text is the setting.
            var missing = scratch.PathOf("nowhere\\ffmpeg.exe");
            w.FfmpegPath.Text = missing;
            Assert.Equal(missing, h.Last.FfmpegPath);
            await w.FindToolsAsync();
            Assert.StartsWith("There's no ffmpeg.exe at that path.", w.FfmpegStatus.Text);
            Assert.Contains((missing, null), h.Looks);

            h.Tools = new Toolbox { SevenZip = sevenZip };
            w.SevenZipPath.Text = "\"" + sevenZip + "\""; // pasted with quotes, as Explorer's "Copy as path" gives
            Assert.Equal(sevenZip, h.Last.SevenZipPath);
            await w.FindToolsAsync();
            Assert.Equal("Found: " + sevenZip, w.SevenZipStatus.Text);

            // Emptied, the program is found automatically again.
            w.FfmpegPath.Text = "";
            Assert.Null(h.Last.FfmpegPath);

            // settings.json edited by hand: the window shows it without handing anything back.
            var count = h.Applied.Count;
            w.Show(new Settings { Hotkey = "Ctrl+Alt+T", Theme = "light", JpegQuality = 50, SevenZipPath = sevenZip, FloatHotkey = "Alt+F2" });
            Assert.Equal(count, h.Applied.Count);
            Assert.Equal("Ctrl+Alt+T", w.MenuShortcut.Text);
            Assert.Equal("Alt+F2", w.ShelfShortcut.Text);
            Assert.True(w.ThemeLight.IsChecked);
            Assert.Equal(50, w.JpegSlider.Value);
            Assert.True(w.ClearShelfShortcut.IsEnabled);

            // The next change starts from the hand-edited settings.
            w.HeicSlider.Value = 40;
            Assert.Equal(("Ctrl+Alt+T", 50, 40), (h.Last.Hotkey, h.Last.JpegQuality, h.Last.HeicQuality));
        });
    }

    [Fact]
    public void Shortcuts_written_out_read_back_the_same()
    {
        foreach (var (modifiers, key) in new[]
                 {
                     (ModifierKeys.Control | ModifierKeys.Shift, Key.Space), (ModifierKeys.Alt, Key.D0), (ModifierKeys.Windows | ModifierKeys.Control, Key.F),
                     (ModifierKeys.Control, Key.OemComma), (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows, Key.F11),
                 })
        {
            var text = Settings.FormatHotkey(modifiers, key);
            Assert.True(Settings.TryParseHotkey(text, out var m, out var k), text);
            Assert.Equal((modifiers, key), (m, k));
        }
        Assert.Equal("Ctrl+Shift+Space", Settings.FormatHotkey(ModifierKeys.Shift | ModifierKeys.Control, Key.Space));
    }
}
