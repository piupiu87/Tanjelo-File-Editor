using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Everything the editors draw can be added, selected, moved and resized from the keyboard, and Tab goes through the
/// boxes, marks or stretches and then on to the next control.
/// </summary>
public class KeyboardEditingTests
{
    [Fact]
    public async Task Redaction_boxes_from_the_keyboard()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 800, 600);
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "redact", [photo]);
            var editor = new RedactEditor(photo, target);
            var window = new EditorWindow(photo, target, editor);
            Ui.Host(window, 1100, 760);
            await window.LoadAsync();
            await Ui.Settle();

            Assert.False(editor.Press(Key.Tab));                       // nothing to go through: Tab moves on
            Assert.True(editor.Press(Key.N));                          // a box in the middle, a quarter of the picture
            Assert.Equal(new RedactBox(300, 225, 200, 150), Assert.Single(editor.Current.Boxes));
            Assert.Equal(0, editor.Selected);
            editor.Press(Key.Right, ModifierKeys.Control);             // a pixel wider
            editor.Press(Key.Down, ModifierKeys.Control | ModifierKeys.Shift);   // 10 taller
            Assert.Equal(new RedactBox(300, 225, 201, 160), editor.Current.Boxes[0]);
            editor.Press(Key.Left, ModifierKeys.Shift);                // moved 10 left
            Assert.Equal(new RedactBox(290, 225, 201, 160), editor.Current.Boxes[0]);
            editor.Press(Key.Insert);
            Assert.Equal(1, editor.Selected);
            Assert.Equal("2 areas · 2 of 2 selected", window.StatusText.Text);

            // Tab goes through them, then lets go so the focus moves on; Shift+Tab comes back from the end.
            editor.Press(Key.Escape);
            Assert.True(editor.Press(Key.Tab));
            Assert.Equal(0, editor.Selected);
            Assert.True(editor.Press(Key.Tab));
            Assert.Equal(1, editor.Selected);
            Assert.False(editor.Press(Key.Tab));
            Assert.Null(editor.Selected);
            Assert.True(editor.Press(Key.Tab, ModifierKeys.Shift));
            Assert.Equal(1, editor.Selected);

            // Sizes stay on the picture and are never less than a pixel.
            for (var i = 0; i < 80; i++) editor.Press(Key.Left, ModifierKeys.Control | ModifierKeys.Shift, repeat: i > 0);
            Assert.Equal(1, editor.Current.Boxes[1].Width);
            editor.Undo();                                             // held keys are one step
            Assert.Equal(200, editor.Current.Boxes[1].Width);
        });
    }

    [Fact]
    public async Task Marks_and_callouts_from_the_keyboard()
    {
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 1200, 600);
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "annotate", [photo]);
            var editor = new AnnotateEditor(photo, target);
            var window = new EditorWindow(photo, target, editor);
            Ui.Host(window, 1100, 760);
            await window.LoadAsync();
            await Ui.Settle();

            // A rectangle in the middle, made bigger from its far corner and moved.
            editor.Press(Key.R);
            Assert.True(editor.Press(Key.Insert));
            var box = Assert.Single(editor.Marks);
            Assert.Equal(MarkKind.Rectangle, box.Kind);
            Assert.Equal((500, 240, 700, 360), (box.Start.X, box.Start.Y, box.End.X, box.End.Y));
            editor.Press(Key.Right, ModifierKeys.Control | ModifierKeys.Shift);
            editor.Press(Key.Up);
            box = editor.Marks[0];
            Assert.Equal((500, 239, 710, 359), (box.Start.X, box.Start.Y, box.End.X, box.End.Y));

            // A callout, made bigger with Ctrl and the arrows.
            editor.Press(Key.N);
            editor.Press(Key.Enter);
            var callout = editor.Marks[1];
            Assert.Equal(MarkKind.Callout, callout.Kind);
            Assert.Equal(1, editor.Selected);
            editor.Press(Key.Up, ModifierKeys.Control | ModifierKeys.Shift);
            Assert.Equal(callout.Size - 10, editor.Marks[1].Size);
            Assert.Contains("2 of 2 selected", window.StatusText.Text);

            // Tab goes through the marks and then on.
            editor.Press(Key.Escape);
            Assert.True(editor.Press(Key.Tab));
            Assert.Equal(0, editor.Selected);
            Assert.True(editor.Press(Key.Tab));
            Assert.False(editor.Press(Key.Tab));
            Assert.Null(editor.Selected);

            // Text opens for typing in the middle.
            editor.Press(Key.T);
            editor.Press(Key.Insert);
            Assert.NotNull(editor.Typing);
        });
    }

    [Fact]
    public async Task A_click_leaves_the_focus_where_it_was_and_Tab_still_reaches_the_button()
    {
        await Ui.Run(async () =>
        {
            var button = new Button { Content = "Play", Style = (Style)Application.Current.FindResource("SubtleButton") };
            var host = new Border { Child = button, Width = 200, Height = 80 };
            using var attached = A11y.Attach(host);
            Assert.True(button.Focusable);
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
            Assert.False(button.Focusable);                            // so the press doesn't take the focus
            await Ui.Settle();
            Assert.True(button.Focusable);
            Assert.Contains(button, A11y.TabOrder(host));
        });
    }
}

/// <summary>The same for the timeline editors, which drive Windows' player, so they run on their own.</summary>
[Collection("Windows player")]
public class TimelineKeyboardEditingTests
{
    [Fact]
    public async Task Stretches_from_the_keyboard()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var song = scratch.Tone("song.wav", seconds: 4);
        await Ui.Run(async () =>
        {
            var (window, editor, _) = await TrimEditorTests.Open(song, "bleep", (file, target) => new BleepEditor(file, target, Env.Tools));
            try
            {
                TimelineTests.Enter(editor.TimeBox, "1");
                Assert.True(editor.Press(Key.N));                      // a second from the playhead
                Assert.Equal([new Bleep(1.0, 2.0)], editor.Current.Bleeps);
                editor.Press(Key.Right, ModifierKeys.Alt);             // moved on a hundredth
                Assert.Equal(new Bleep(1.01, 2.01), editor.Current.Bleeps[0]);
                editor.Press(Key.Right, ModifierKeys.Alt | ModifierKeys.Shift);   // and a second
                editor.Press(Key.Left, ModifierKeys.Control);          // its end back a hundredth
                Assert.Equal(new Bleep(2.01, 3.0), editor.Current.Bleeps[0]);
                Assert.Equal(1.0, editor.Playhead, 3);                 // the playhead stayed

                Assert.False(editor.Press(Key.Tab));                   // the only one was selected: Tab moves on
                Assert.Null(editor.Selected);
                Assert.True(editor.Press(Key.Tab));
                Assert.Equal(0, editor.Selected);
                Assert.Equal("1 stretch · one selected", window.StatusText.Text);
            }
            finally
            {
                editor.Close();
            }
        });
    }

    [Fact]
    public async Task Video_boxes_from_the_keyboard()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var clip = scratch.Numbered("clip.mp4", seconds: 2);       // 320 × 240
        await Ui.Run(async () =>
        {
            var (window, editor, _) = await TrimEditorTests.Open(clip, "redact", (file, target) => new VideoRedactEditor(file, target, Env.Tools));
            try
            {
                TimelineTests.Enter(editor.TimeBox, "1");
                await editor.Seek();
                Assert.True(editor.Press(Key.Insert));                 // in the middle, from here to the end
                var box = Assert.Single(editor.Current.Boxes);
                Assert.Equal((120, 90, 80, 60, 1.0, (double?)null), (box.X, box.Y, box.Width, box.Height, box.From, box.To));
                editor.Press(Key.Right, ModifierKeys.Alt | ModifierKeys.Shift);    // moved 10 right
                editor.Press(Key.Down, ModifierKeys.Control);                     // a pixel taller
                box = editor.Current.Boxes[0];
                Assert.Equal((130, 90, 80, 61), (box.X, box.Y, box.Width, box.Height));
                Assert.Equal(1.0, editor.Playhead, 3);                 // the arrows with Alt or Ctrl don't step frames

                editor.Press(Key.Right);                               // plain arrows still do
                Assert.Equal(26, editor.Frame);
                Assert.False(editor.Press(Key.Tab));
                Assert.Null(editor.Selected);
            }
            finally
            {
                editor.Close();
            }
        });
    }
}
