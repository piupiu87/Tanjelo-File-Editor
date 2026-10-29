using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Tanjelo.Editors;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Every window, as a screen reader and the keyboard find it: each control named, every one reached by Tab in a
/// sensible order, and drawn in a Windows contrast theme with that theme's colours.
/// </summary>
public class AccessibilityTests
{
    sealed class FakeWindowsSettings : IWindowsSettings
    {
        public bool StartsWithWindows { get; set; }
        public bool InSendToMenu { get; set; }
        public bool CanUseWindows11Menu { get; set; }
        public bool InWindows11Menu { get; set; }
    }

    /// <summary>Renders the host in a contrast theme and checks its background took the theme's window colour.</summary>
    internal static void RenderInContrast(FrameworkElement host, string name, Theme.Contrast contrast)
    {
        // Changed brushes reach content only while it's in a window, so it's put in one if it isn't already.
        using var attached = System.Windows.PresentationSource.FromVisual(host) is null ? A11y.Attach(host) : null;
        Theme.ContrastOverride = contrast;
        try
        {
            Ui.UseTheme("light", host);
            var render = Ui.Render(host, name);
            Assert.True(Ui.Near(contrast.Window, Ui.Pixel(render, 2, 2)), $"{name}: the background should be the contrast theme's window colour");
            Assert.Equal(contrast.WindowText, Ui.Colour("TextPrimary"));
            Assert.Equal(contrast.Highlight, Ui.Colour("Accent"));
        }
        finally
        {
            Theme.ContrastOverride = null;
            Ui.UseTheme("light", host);
        }
    }

    [Fact]
    public async Task Settings_window()
    {
        await Ui.Run(async () =>
        {
            var window = new SettingsWindow(new Settings { FloatHotkey = "Ctrl+Shift+F" }, _ => { }, new FakeWindowsSettings { CanUseWindows11Menu = true }, (_, _) => new Toolbox());
            window.ShowUpdate("Tanjelo 1.2.0 is out (checked today at 09:30).", UpdateCheck.ReleasesPage);
            var host = Ui.Host(window, 600, 1600);
            using var attached = A11y.Attach(host);
            await Ui.Settle();
            var order = A11y.Check(host, "settings");
            A11y.InOrder(order, window.MenuShortcut, window.ShelfShortcut, window.ClearShelfShortcut, window.ThemeSystem, window.ThemeDark,
                window.JpegSlider, window.DpiSlider, window.FfmpegPath, window.FfmpegBrowse, window.SevenZipPath, window.StartupCheck,
                window.SendToCheck, window.Windows11MenuCheck, window.CheckUpdatesBox, window.UpdateLink, window.OpenFileButton, window.DoneButton);
            RenderInContrast(host, "settings-contrast", Theme.Contrast.Aquatic);
            RenderInContrast(host, "settings-contrast-desert", Theme.Contrast.Desert);
        });
    }

    [Fact]
    public async Task The_menu_is_named_and_its_entries_are_read_out_as_the_keys_move()
    {
        using var scratch = new Scratch();
        var photo = scratch.Image("photo.png");
        await Ui.Run(async () =>
        {
            var engine = Env.Engine();
            var menu = new FormatMenuWindow([photo], engine.GetMenu([photo]), (ModifierKeys.Control | ModifierKeys.Shift, Key.Space));
            var host = Ui.Host(menu, 330, 700);
            using var attached = A11y.Attach(host);
            await Ui.Settle();

            // The keys stay in the search box: the tabs are switched with Tab and the entries chosen with the arrows.
            var order = A11y.Check(host, "menu", c => c is RadioButton || c is ListBoxItem);
            Assert.Equal([menu.Search], order);
            Assert.Equal("Convert to", AutomationProperties.GetName(menu.Search));
            Assert.StartsWith("Convert, ", AutomationProperties.GetName(menu.ConvertTab));
            var first = (ListBoxItem)menu.List.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.False(string.IsNullOrEmpty(AutomationProperties.GetName(first)));

            Assert.True(menu.Press(Key.Down, ModifierKeys.None));
            var entry = (FormatMenuWindow.Entry)menu.List.SelectedItem;
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(menu.Spoken));
            Assert.Equal($"{entry.Label}, {entry.Description}, 2 of {menu.List.Items.Count}", AutomationProperties.GetName(menu.Spoken));

            Assert.True(menu.Press(Key.Tab, ModifierKeys.None));
            Assert.True(menu.ToolsTab.IsChecked);
            Assert.Equal("Find a tool", AutomationProperties.GetName(menu.Search));
            Ui.Render(host, "menu-light");
            RenderInContrast(host, "menu-contrast", Theme.Contrast.Aquatic);
        });
    }

    [Fact]
    public async Task Every_options_dialog_for_pictures_is_named_and_reached_by_Tab()
    {
        using var scratch = new Scratch();
        var photo = scratch.Image("photo.png", 320, 240);
        var second = scratch.Image("second.png", 320, 240, System.Drawing.Color.SteelBlue);
        await Ui.Run(async () =>
        {
            var engine = Env.Engine();
            var checkedAny = 0;
            foreach (var files in new[] { new[] { photo }, [photo, second] })
            {
                foreach (var target in engine.GetMenu(files).Targets.Where(t => t.Options.Count > 0 && t.Editor is null))
                {
                    var dialog = new OptionsWindow(engine, files, target, new Dictionary<string, string>());
                    var host = Ui.Host(dialog, 700, 900);
                    using (A11y.Attach(host))
                    {
                        await Ui.Settle();
                        var order = A11y.Check(host, $"options for {target.Key}");
                        // The close button at the top, the fields top to bottom, then Cancel and the button that runs the tool.
                        Assert.Equal(dialog.CloseButton, order[0]);
                        A11y.InOrder(order, dialog.CancelButton, dialog.RunButton);
                        if (target.Key == "background") RenderInContrast(host, "options-background-contrast", Theme.Contrast.Desert);
                    }
                    dialog.Close();
                    checkedAny++;
                }
            }
            Assert.True(checkedAny >= 5, $"only {checkedAny} options dialogs were checked");
        });
    }

    [Fact]
    public async Task Every_options_dialog_for_video_and_sound_is_named_and_reached_by_Tab()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Video("clip.mp4");
        var other = scratch.Video("other.mp4");
        var tone = scratch.Tone("tone.wav");
        await Ui.Run(async () =>
        {
            var engine = Env.Engine();
            foreach (var files in new[] { new[] { clip }, [clip, other], [tone] })
                foreach (var target in engine.GetMenu(files).Targets.Where(t => t.Options.Count > 0 && t.Editor is null))
                {
                    var dialog = new OptionsWindow(engine, files, target, new Dictionary<string, string>());
                    var host = Ui.Host(dialog, 700, 900);
                    using (A11y.Attach(host))
                    {
                        await Ui.Settle();
                        A11y.Check(host, $"options for {target.Key}");
                    }
                    dialog.Close();
                }
        });
    }

    [Fact]
    public async Task Result_cards_are_named_and_announce_themselves()
    {
        using var scratch = new Scratch();
        var photo = scratch.Image("photo.png");
        var output = scratch.Image("photo.jpg");
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "jpg", [photo]);
            var card = new ProgressToast();
            card.ShowRunning(target, 3);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(card.Card));
            Assert.Equal($"{target.RunningText} 3 items", AutomationProperties.GetName(card.Card));

            card.ShowResult(target, [photo], new BatchResult([output], [], [], false));
            Assert.Equal($"{target.DoneText}. photo.jpg", AutomationProperties.GetName(card.Card));
            var host = Ui.Host(card, 360, 160);
            using var attached = A11y.Attach(host);
            await Ui.Settle();
            var order = A11y.Check(host, "result card");
            A11y.InOrder(order, card.SecondaryButton, card.PrimaryButton, card.CloseButton);
            RenderInContrast(host, "card-contrast", Theme.Contrast.Aquatic);
        });
    }

    [Fact]
    public async Task The_shelf_names_its_files()
    {
        using var scratch = new Scratch();
        var a = scratch.Image("a.png");
        var b = scratch.Image("b.png");
        await Ui.Run(async () =>
        {
            var shelf = new Shelf();
            shelf.Add([a, b]);
            var window = new FloatWindow(shelf, "Ctrl+Shift+Space");
            var host = Ui.Host(window, 248, 260);
            using var attached = A11y.Attach(host);
            await Ui.Settle();
            // Tab reaches the list once; the arrows go through its files and Delete takes one off.
            var order = A11y.Check(host, "shelf", c => c is ListBoxItem || c is Button { IsTabStop: false });
            A11y.InOrder(order, window.ClearButton, window.HideButton);
            var item = (ListBoxItem)window.List.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.Equal("a.png", AutomationProperties.GetName(item));
            RenderInContrast(host, "shelf-contrast", Theme.Contrast.Desert);
        });
    }

    static async Task<(EditorWindow Window, Editor Editor, FrameworkElement Host)> OpenEditor(string file, string key, Func<string, Target, Editor> make)
    {
        var target = Env.Offered(Env.Engine(), key, [file]);
        var editor = make(file, target);
        var window = new EditorWindow(file, target, editor);
        var host = Ui.Host(window, 1100, 760);
        await window.LoadAsync();
        await Ui.Settle();
        return (window, editor, host);
    }

    [Fact]
    public async Task The_picture_and_PDF_editors_are_named_and_reached_by_Tab()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.jpg");
        var pdf = scratch.Pdf("report.pdf", pages: 3);
        await Ui.Run(async () =>
        {
            foreach (var (file, key, make) in new (string, string, Func<string, Target, Editor>)[]
                     {
                         (pdf, "organize-pdf", (f, t) => new PageEditor(f, t)),
                         (photo, "redact", (f, t) => new RedactEditor(f, t)),
                         (photo, "annotate", (f, t) => new AnnotateEditor(f, t)),
                         (photo, "adjust", (f, t) => new PhotoEditor(f, t)),
                     })
            {
                var (window, editor, host) = await OpenEditor(file, key, make);
                using (A11y.Attach(host))
                {
                    await Ui.Settle();
                    var order = A11y.Check(host, $"{key} editor");
                    // The frame's buttons at the top, the editor's tools and picture, then Cancel (Save waits for a change).
                    A11y.InOrder(order, window.CloseButton, window.CancelButton);
                    if (editor.Canvas is { } canvas) A11y.InOrder(order, canvas, window.CancelButton);
                }
                RenderInContrast(host, $"{key}-contrast", Theme.Contrast.Aquatic);
                window.Cancel();
            }
        });
    }
}

/// <summary>The audio and video editors, which drive Windows' player, so they run on their own.</summary>
[Collection("Windows player")]
public class TimelineAccessibilityTests
{
    [Fact]
    public async Task The_audio_and_video_editors_are_named_and_reached_by_Tab()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 2);
        var tone = scratch.Tone("tone.wav");
        await Ui.Run(async () =>
        {
            async Task Check<T>(string file, string key, Func<string, Target, T> make) where T : Editor, ITimelineEditor
            {
                var (window, editor, host) = await TrimEditorTests.Open(file, key, make);
                using (A11y.Attach(host))
                {
                    await Ui.Settle();
                    var order = A11y.Check(host, $"{key} editor on {Path.GetFileName(file)}");
                    A11y.InOrder(order, editor.View, window.CancelButton);
                }
                AccessibilityTests.RenderInContrast(host, $"{key}-{Path.GetExtension(file).TrimStart('.')}-contrast", Theme.Contrast.Desert);
                window.Cancel();
            }
            await Check(clip, "trim", (f, t) => new TrimEditor(f, t, Env.Tools));
            await Check(tone, "trim", (f, t) => new TrimEditor(f, t, Env.Tools));
            await Check(tone, "bleep", (f, t) => new BleepEditor(f, t, Env.Tools));
            await Check(clip, "redact", (f, t) => new VideoRedactEditor(f, t, Env.Tools));
        });
    }
}
