using System.Windows.Media.Imaging;
using ImageMagick;
using Tanjelo.Core.Converters;
using Tanjelo.Editors;

namespace Tanjelo.Core.Tests;

/// <summary>The photo editor off-screen: sliders, the live preview, the original, undo and Save.</summary>
public class PhotoEditorTests
{
    static double Brightness(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        double sum = 0;
        for (var i = 0; i < pixels.Length; i += 4) sum += 0.0722 * pixels[i] + 0.7152 * pixels[i + 1] + 0.2126 * pixels[i + 2];
        return sum / (pixels.Length / 4) / 255;
    }

    [Fact]
    public async Task Sliders_change_the_preview_with_one_undo_step_per_drag()
    {
        using var scratch = new Scratch();
        var photo = scratch.PathOf("photo.jpg");
        using (var dark = new MagickImage("plasma:fractal", 2400, 1600))
        {
            dark.Evaluate(Channels.RGB, EvaluateOperator.Multiply, 0.45); // underexposed
            dark.Write(photo);
        }
        await Ui.Run(async () =>
        {
            var target = Env.Offered(Env.Engine(), "adjust", [photo]);
            var editor = new PhotoEditor(photo, target);
            var window = new EditorWindow(photo, target, editor);
            var host = Ui.Host(window, 1200, 860);
            await window.LoadAsync();
            await Ui.Settle();
            Ui.Layout(host);
            Assert.EndsWith("2400 × 1600", window.Subheading.Text);
            Assert.Equal(1600, editor.Original!.PixelWidth); // the preview develops a screen-sized copy
            Assert.False(window.SaveButton.IsEnabled);

            // One drag of the exposure slider, through several values, is one step.
            var exposure = editor.SliderFor("exposure");
            editor.StartDrag();
            foreach (var value in new[] { 0.2, 0.5, 0.9 }) exposure.Value = value;
            editor.EndDrag();
            await editor.SettleAsync();
            Assert.Equal(0.9, editor.Settings.Exposure);
            Assert.True(Brightness(editor.Shown!) > Brightness(editor.Original!) + 0.05, "the preview is brighter");
            Assert.True(window.SaveButton.IsEnabled);
            TestContext.Current.TestOutputHelper?.WriteLine($"exposure preview: {editor.LastMilliseconds} ms");
            Assert.True(editor.LastMilliseconds < 500, $"the preview took {editor.LastMilliseconds} ms");

            editor.SliderFor("vibrance").Value = 40;                       // a click: a step of its own
            editor.SliderFor("vignette").Value = 30;
            await editor.SettleAsync();
            editor.ShowOriginal(true);
            Assert.Same(editor.Original, editor.Shown);
            Assert.Equal("The original", window.StatusText.Text);
            editor.ShowOriginal(false);
            Assert.NotSame(editor.Original, editor.Shown);

            Ui.Layout(host);
            Ui.Render(host, "photo-light");
            Ui.UseTheme("dark", host);
            await Ui.Settle();
            Ui.Render(host, "photo-dark");

            editor.Undo();
            editor.Undo();
            Assert.Equal(0, editor.SliderFor("vibrance").Value);              // the sliders follow undo
            Assert.Equal(0.9, editor.Settings.Exposure);
            editor.Undo();
            Assert.True(editor.Settings.IsNone);
            Assert.Equal(0, exposure.Value);
            Assert.False(window.SaveButton.IsEnabled);

            editor.RunAuto();
            Assert.True(editor.Settings.Exposure > 0.2, $"auto brightens a dark photo: {editor.Settings.Exposure}");
            await editor.SettleAsync();

            window.Save();
            var saved = await window.EditResult;
            var result = await Env.Engine().RunAsync([photo], saved!, ct: TestContext.Current.CancellationToken);
            using var before = new MagickImage(photo);
            using var after = new MagickImage(Assert.Single(result.Outputs));
            Assert.True(after.Statistics().GetChannel(PixelChannel.Green)!.Mean > before.Statistics().GetChannel(PixelChannel.Green)!.Mean * 1.15,
                "the saved photo is brighter, like the preview");
        });
    }
}
