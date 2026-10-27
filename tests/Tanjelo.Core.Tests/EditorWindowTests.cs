using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tanjelo.Editors;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace Tanjelo.Core.Tests;

/// <summary>The editor window off-screen, with an editor that puts dots on a picture: frame, canvas, undo, Cancel and Save.</summary>
public class EditorWindowTests
{
    /// <summary>Clicking the canvas puts a dot there, in the picture's pixels.</summary>
    sealed class DotEditor(string file) : Editor<ImmutableList<Point>>, ICanvasTool
    {
        public static readonly Target Entry = new("dots", "Dots…", "Dots on a picture", Section.Image) { Kind = TargetKind.Tool };

        readonly ZoomCanvas _canvas = new();

        public override FrameworkElement Main => _canvas;
        public override ZoomCanvas Canvas => _canvas;
        public override string Status => $"{State.Count} dots";
        public ImmutableList<Point> Dots => State;

        public override Task LoadAsync(CancellationToken cancel)
        {
            var picture = BitmapFrame.Create(new Uri(file), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            _canvas.Show(picture, new Size(picture.PixelWidth, picture.PixelHeight));
            _canvas.Tool = this;
            Begin([]);
            return Task.CompletedTask;
        }

        public override Target Result() => Entry.WithParameters(new Dictionary<string, string>
        {
            ["dots"] = string.Join(";", State.Select(p => $"{p.X:0},{p.Y:0}")),
        });

        protected override void Show(ImmutableList<Point> dots)
        {
            _canvas.World.Children.Clear();
            foreach (var dot in dots)
            {
                var mark = new System.Windows.Shapes.Ellipse { Width = 20, Height = 20, Fill = Brushes.Black };
                System.Windows.Controls.Canvas.SetLeft(mark, dot.X - 10);
                System.Windows.Controls.Canvas.SetTop(mark, dot.Y - 10);
                _canvas.World.Children.Add(mark);
            }
        }

        public void Down(Point at, ModifierKeys modifiers, int clicks) => Commit(State.Add(at));
        public void Move(Point at) { }
        public void Up(Point at) { }
        public Cursor? CursorAt(Point at) => Cursors.Cross;
    }

    [Fact]
    public async Task The_window_shows_the_picture_fitted_and_clicks_land_on_its_pixels()
    {
        using var scratch = new Scratch();
        var file = scratch.Image("photo.png", 400, 300, System.Drawing.Color.FromArgb(30, 120, 220));
        await Ui.Run(async () =>
        {
            var editor = new DotEditor(file);
            var window = new EditorWindow(file, DotEditor.Entry, editor);
            var host = Ui.Host(window, 1000, 700);
            await window.LoadAsync();
            await Ui.Settle();
            Assert.False(window.SaveButton.IsEnabled);   // nothing to save yet
            Assert.False(window.UndoButton.IsEnabled);
            Assert.Equal("100%", window.ZoomText.Text);   // small enough to show whole at 100%
            Assert.Equal(Visibility.Visible, window.ZoomBar.Visibility);

            var canvas = editor.Canvas;
            var middle = new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2);
            var render = Ui.Render(host, "editor-light");
            var onCanvas = canvas.TranslatePoint(middle, host);
            Assert.True(Ui.Near(Color.FromRgb(30, 120, 220), Ui.Pixel(render, (int)onCanvas.X, (int)onCanvas.Y)), "the picture should be in the middle");
            Assert.True(Ui.Near(Ui.Colour("Surface"), Ui.Pixel(render, 4, 4)), "the frame should be the surface colour");

            // A click in the middle of the canvas is the middle of the picture.
            canvas.PointerDown(middle, MouseButton.Left, ModifierKeys.None);
            canvas.PointerUp(middle);
            Assert.Equal(new Point(200, 150), Assert.Single(editor.Dots));
            Assert.True(window.SaveButton.IsEnabled);
            Assert.Equal("1 dots", window.StatusText.Text);

            editor.Undo();
            Assert.Empty(editor.Dots);
            Assert.False(window.SaveButton.IsEnabled);
            editor.Redo();
            Assert.Single(editor.Dots);

            // The wheel zooms around the pointer; Space and a drag pan instead of drawing.
            var pointer = new Point(middle.X + 60, middle.Y - 40);
            var pixel = canvas.View.ToPicture(pointer);
            canvas.Wheel(pointer, 240);
            var still = canvas.View.ToPicture(pointer);
            Assert.Equal((Math.Round(pixel.X, 6), Math.Round(pixel.Y, 6)), (Math.Round(still.X, 6), Math.Round(still.Y, 6)));
            Assert.Equal("144%", window.ZoomText.Text);
            canvas.SpaceHeld = true;
            canvas.PointerDown(pointer, MouseButton.Left, ModifierKeys.None);
            canvas.PointerMove(new Point(pointer.X + 30, pointer.Y));
            canvas.PointerUp(new Point(pointer.X + 30, pointer.Y));
            canvas.SpaceHeld = false;
            Assert.Single(editor.Dots);
            canvas.Fit();
            Assert.Equal("100%", window.ZoomText.Text);

            Ui.UseTheme("dark", host);
            await Ui.Settle();
            var dark = Ui.Render(host, "editor-dark");
            Assert.True(Ui.Near(Ui.Colour("Surface"), Ui.Pixel(dark, 4, 4)), "the frame should follow the dark theme");
        });
    }

    [Fact]
    public async Task Cancel_asks_only_when_something_changed_and_Save_hands_back_the_edits()
    {
        using var scratch = new Scratch();
        var file = scratch.Image("photo.png", 400, 300);
        await Ui.Run(async () =>
        {
            var unchanged = new EditorWindow(file, DotEditor.Entry, new DotEditor(file));
            Ui.Host(unchanged, 900, 600);
            await unchanged.LoadAsync();
            var canceled = unchanged.EditResult;
            unchanged.Cancel();
            Assert.Null(await canceled);

            var editor = new DotEditor(file);
            var window = new EditorWindow(file, DotEditor.Entry, editor);
            var host = Ui.Host(window, 900, 600);
            await window.LoadAsync();
            await Ui.Settle();
            var canvas = editor.Canvas;
            var at = canvas.View.ToCanvas(new Point(100, 50));
            canvas.PointerDown(at, MouseButton.Left, ModifierKeys.None);
            canvas.PointerUp(at);

            window.Cancel();
            Assert.Equal(Visibility.Visible, window.Confirm.Visibility);
            Assert.False(window.EditResult.IsCompleted);
            Ui.Render(host, "editor-confirm-light");
            window.KeepButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Collapsed, window.Confirm.Visibility);

            window.Save();
            var saved = await window.EditResult;
            Assert.Equal("100,50", saved?.Parameters["dots"]);
        });
    }
}
