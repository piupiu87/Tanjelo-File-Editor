using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Tanjelo.Editors;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;

namespace Tanjelo.Core.Tests;

/// <summary>
/// Every window in a pseudo-language: each string from the resource files bracketed, accented and a third longer, as a
/// translation might be. Text without brackets was left in the code; text cut off at its edge needs a layout that grows.
/// It runs in a process of its own, started with TANJELO_PSEUDO set, so no English string is held from an earlier test.
/// </summary>
public class PseudoLanguageTests
{
    const string Switch = "TANJELO_PSEUDO";

    static bool InPseudo => Environment.GetEnvironmentVariable(Switch) == "1";

    /// <summary>In the pseudo-language process, every string is changed as it's read, before anything reads one.</summary>
    [ModuleInitializer]
    internal static void UsePseudoLanguage()
    {
        if (!InPseudo) return;
        Strings.Transform = Pseudo;
        Tanjelo.Strings.Transform = Pseudo;
    }

    /// <summary>"Settings" becomes "[Šéţţîñĝš ~~~]": placeholders such as {0} and Open-dialog filters stay as they are.</summary>
    internal static string Pseudo(string text)
    {
        if (text.Contains('|')) return text;
        var result = new StringBuilder("[");
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '{') depth++;
            result.Append(depth > 0 ? c : Accented(c));
            if (c == '}') depth = Math.Max(0, depth - 1);
        }
        var letters = text.Count(char.IsLetter);
        return result.Append(' ').Append('~', Math.Max(2, letters / 3)).Append(']').ToString();
    }

    static char Accented(char c) => c switch
    {
        'a' => 'á', 'e' => 'é', 'i' => 'î', 'o' => 'ô', 'u' => 'ü', 'c' => 'ç', 'n' => 'ñ', 's' => 'š', 'y' => 'ý', 'z' => 'ž',
        'A' => 'Å', 'E' => 'É', 'I' => 'Î', 'O' => 'Ö', 'U' => 'Û', 'C' => 'Ç', 'N' => 'Ñ', 'S' => 'Š', 'Y' => 'Ý', 'Z' => 'Ž',
        _ => c,
    };

    [Fact]
    public async Task Every_window_holds_up_in_a_pseudo_language()
    {
        Assert.SkipWhen(InPseudo, "This is the outer test; the check itself runs in its own process.");
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            ArgumentList = { "-method", $"{typeof(PseudoLanguageTests).FullName}.{nameof(Check_every_window_in_the_pseudo_language)}" },
            Environment = { [Switch] = "1" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var run = Process.Start(start)!;
        var output = run.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var errors = run.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            await run.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            run.Kill();
            throw;
        }
        var report = await output + await errors;
        Assert.True(report.Contains("Total: 1, Errors: 0, Failed: 0, Skipped: 0"), report);
    }

    sealed class FakeWindowsSettings : IWindowsSettings
    {
        public bool StartsWithWindows { get; set; }
        public bool InSendToMenu { get; set; }
        public bool CanUseWindows11Menu { get; set; }
        public bool InWindows11Menu { get; set; }
    }

    [Fact]
    public async Task Check_every_window_in_the_pseudo_language()
    {
        Assert.SkipUnless(InPseudo, $"Runs in its own process, started by the test beside it with {Switch} set.");
        Assert.StartsWith("[", Strings.Compress);
        using var scratch = new Scratch();
        var photo = scratch.Photo("photo.png", 640, 480);
        var second = scratch.Image("second.png", 320, 240);
        var pdf = scratch.Pdf("report.pdf", pages: 3);
        // Made here: ffmpeg is waited for, which would hold up the UI thread the windows need.
        var clip = Env.HasFfmpeg ? scratch.Numbered("clip.mp4", seconds: 2) : null;
        var tone = Env.HasFfmpeg ? scratch.Tone("tone.wav") : null;
        // Names, sizes and numbers that come from the files and settings rather than the resource files.
        string[] data = ["photo.png", "second.png", "report.pdf", "Ctrl+Shift+Space", "Ctrl+Shift+F", "photo.jpg", "a.png", "b.png"];
        var problems = new List<string>();
        await Ui.Run(async () =>
        {
            async Task Check(string name, Window window, double width, double height, Func<Task>? load = null)
            {
                var host = Ui.Host(window, width, height);
                // Loaded before it goes in the hidden window: Windows' player doesn't start for a video already in one.
                if (load is not null) await load();
                using (A11y.Attach(host))
                {
                    await Ui.Settle();
                    Ui.Layout(host);
                    problems.AddRange(Problems(host, name, data));
                    Ui.Render(host, "pseudo-" + name);
                }
            }

            await Check("settings", new SettingsWindow(new Settings { FloatHotkey = "Ctrl+Shift+F" }, _ => { }, new FakeWindowsSettings { CanUseWindows11Menu = true }, (_, _) => new Toolbox()), 600, 1700);
            var engine = Env.Engine();
            await Check("menu", new FormatMenuWindow([photo], engine.GetMenu([photo])), 330, 700);
            var card = new ProgressToast();
            card.ShowResult(Env.Offered(engine, "jpg", [photo]), [photo], new BatchResult([photo], [], [], false));
            await Check("card", card, 360, 200);
            var shelf = new Shelf();
            shelf.Add([photo, second]);
            await Check("shelf", new FloatWindow(shelf, "Ctrl+Shift+Space"), 248, 300);
            foreach (var files in new[] { new[] { photo }, [photo, second] })
                foreach (var target in engine.GetMenu(files).Targets.Where(t => t.Options.Count > 0 && t.Editor is null))
                    await Check("options-" + target.Key + (files.Length > 1 ? "-2" : ""), new OptionsWindow(engine, files, target, new Dictionary<string, string>()), 720, 1000);
            foreach (var (file, key, make) in new (string, string, Func<string, Target, Editor>)[]
                     {
                         (pdf, "organize-pdf", (f, t) => new PageEditor(f, t)),
                         (photo, "redact", (f, t) => new RedactEditor(f, t)),
                         (photo, "annotate", (f, t) => new AnnotateEditor(f, t)),
                         (photo, "adjust", (f, t) => new PhotoEditor(f, t)),
                     })
            {
                var target = Env.Offered(engine, key, [file]);
                var window = new EditorWindow(file, target, make(file, target));
                await Check(key, window, 1100, 760, window.LoadAsync);
            }
            if (clip is null || tone is null) return;
            foreach (var (file, key, make) in new (string, string, Func<string, Target, Editor>)[]
                     {
                         (clip, "trim", (f, t) => new TrimEditor(f, t, Env.Tools)),
                         (tone, "bleep", (f, t) => new BleepEditor(f, t, Env.Tools)),
                         (clip, "redact", (f, t) => new VideoRedactEditor(f, t, Env.Tools)),
                     })
            {
                var target = Env.Offered(engine, key, [file]);
                var editor = make(file, target);
                var window = new EditorWindow(file, target, editor);
                await Check($"{key}-{Path.GetExtension(file).TrimStart('.')}", window, 1100, 760, async () =>
                {
                    await window.LoadAsync();
                    await ((ITimelineEditor)editor).Loaded;
                });
                editor.Close();
            }
        });
        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n" + string.Join("\n", problems));
    }

    /// <summary>Text on show that has no brackets, and text cut off by the space it was given or by what it's inside.</summary>
    static IEnumerable<string> Problems(FrameworkElement host, string window, IReadOnlyCollection<string> data)
    {
        foreach (var block in Descendants(host).OfType<TextBlock>())
        {
            if (!block.IsVisible || block.ActualWidth <= 0) continue;
            var text = block.Text;
            if (!text.Any(char.IsLetter) || text.All(c => c >= '' && c <= '' || !char.IsLetter(c))) continue;
            // A key cap is part of a bracketed list of keys, such as "[Insert" and "N ~~]".
            if (!text.Contains('[') && !text.Contains(']') && !data.Any(d => text.Contains(d, StringComparison.OrdinalIgnoreCase)) && !IsData(text))
                yield return $"{window}: not from the resource files: \"{text}\"";
            if (block.TextTrimming != TextTrimming.None || block.TextWrapping != TextWrapping.NoWrap) continue;
            var natural = new FormattedText(text, CultureInfo.CurrentUICulture, block.FlowDirection,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, System.Windows.Media.Brushes.Black,
                VisualTreeHelper.GetDpi(block).PixelsPerDip).WidthIncludingTrailingWhitespace;
            if (natural > block.ActualWidth + 1)
                yield return $"{window}: squeezed ({natural:0} px in {block.ActualWidth:0}): \"{text}\"";
            // Cut off where it runs past something it's inside, up to the window itself, or past the cell it was given:
            // WPF clips an element to its layout slot while its own width stays that of its whole text.
            for (DependencyObject? outer = block; outer is not null && outer != host; outer = VisualTreeHelper.GetParent(outer))
            {
                if (outer is not FrameworkElement { ActualWidth: > 0 } box || VisualTreeHelper.GetParent(box) is Canvas) continue;
                var left = block.TranslatePoint(new Point(0, 0), box).X;
                var right = block.TranslatePoint(new Point(block.ActualWidth, 0), box).X;
                var slot = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot(box).Width - box.Margin.Left - box.Margin.Right;
                var room = Math.Min(box.ActualWidth, slot > 0 ? slot : box.ActualWidth);
                if (right <= room + 1 && left >= -1) continue;
                yield return $"{window}: cut off by a {box.GetType().Name} ({left:0} to {right:0} px of {room:0}): \"{text}\"";
                break;
            }
        }
    }

    /// <summary>Numbers, times, sizes, colours, file types, paths and key letters, which needn't be translated.</summary>
    static bool IsData(string text) =>
        System.Text.RegularExpressions.Regex.IsMatch(text, @"^[\d\s.,:×x%#A-F·/+\-–()]*(px|KB|MB|GB|bytes|DPI|Hz|fps|s)?$")
        || System.Text.RegularExpressions.Regex.IsMatch(text, @"^[A-Z0-9.]{1,5}$")
        || text.Contains(":\\");

    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var below in Descendants(child)) yield return below;
        }
    }
}
