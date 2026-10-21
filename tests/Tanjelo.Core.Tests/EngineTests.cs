namespace Tanjelo.Core.Tests;

public class EngineTests
{
    [Fact]
    public void A_single_image_is_offered_other_formats_but_not_its_own()
    {
        using var scratch = new Scratch();
        var keys = Env.Keys(scratch.Image("a.png"));

        Assert.Contains("jpg", keys);
        Assert.Contains("webp", keys);
        Assert.Contains("pdf", keys);
        Assert.DoesNotContain("png", keys);
        Assert.DoesNotContain("combine-pdf", keys);
    }

    [Fact]
    public void Image_formats_come_before_other_sections_for_an_image()
    {
        using var scratch = new Scratch();
        var menu = Env.Engine().GetMenu([scratch.Image("a.png")]);

        var firstOther = menu.Targets.ToList().FindIndex(t => t.Section != Section.Image);
        Assert.True(firstOther > 0);
        Assert.All(menu.Targets.Skip(firstOther), t => Assert.NotEqual(Section.Image, t.Section));
    }

    [Fact]
    public void A_mixed_selection_keeps_each_files_own_format_on_offer()
    {
        using var scratch = new Scratch();
        var keys = Env.Keys(scratch.Image("a.png"), scratch.Image("b.jpg"));

        Assert.Contains("png", keys);
        Assert.Contains("jpg", keys);
        Assert.Contains("combine-pdf", keys);
    }

    [Fact]
    public void Only_formats_every_file_supports_are_offered()
    {
        using var scratch = new Scratch();
        var keys = Env.Keys(scratch.Image("a.png"), scratch.Pdf("b.pdf"));

        Assert.Contains("jpg", keys);       // both can become JPG
        Assert.DoesNotContain("webp", keys); // the PDF can't
    }

    [Fact]
    public void Folders_and_unknown_files_can_only_be_compressed()
    {
        using var scratch = new Scratch();
        Assert.All(Env.Keys(scratch.Folder("photos", "a.txt")), key => Assert.StartsWith("compress-", key));
        Assert.All(Env.Keys(scratch.Text("data.xyz", "?")), key => Assert.StartsWith("compress-", key));
    }

    [Fact]
    public void Missing_tools_hide_their_formats_and_explain_why()
    {
        using var scratch = new Scratch();
        var clip = scratch.Text("clip.mp4", "not really a video");
        var engine = new ConverterEngine(new Toolbox());

        var menu = engine.GetMenu([clip]);
        Assert.DoesNotContain(menu.Targets, t => t.Section is Section.Video or Section.Audio);
        Assert.Contains(menu.Hints, h => h.Contains("ffmpeg"));
    }

    [Fact]
    public async Task Files_already_in_the_target_format_are_skipped()
    {
        using var scratch = new Scratch();
        var png = scratch.Image("a.png");
        var jpg = scratch.Image("b.jpg");

        var result = await Env.Run("jpg", png, jpg);

        Assert.Equal([scratch.PathOf("a.jpg")], result.Outputs);
        Assert.Equal([jpg], result.Skipped);
    }

    [Fact]
    public async Task A_failing_file_doesnt_stop_the_rest_of_the_batch()
    {
        using var scratch = new Scratch();
        var good = scratch.Image("good.png");
        var bad = scratch.Text("bad.png", "this is not a PNG");
        var engine = Env.Engine();

        var result = await engine.RunAsync([bad, good], Env.Offered(engine, "jpg", [bad, good]), ct: TestContext.Current.CancellationToken);

        Assert.Equal([scratch.PathOf("good.jpg")], result.Outputs);
        var failure = Assert.Single(result.Failures);
        Assert.Equal(bad, failure.Input);
        Assert.Contains("valid image", failure.Message);
        Assert.False(File.Exists(scratch.PathOf("bad.jpg")));
    }

    [Fact]
    public async Task Canceling_stops_the_batch()
    {
        using var scratch = new Scratch();
        var inputs = Enumerable.Range(1, 3).Select(i => scratch.Image($"{i}.png")).ToArray();
        var engine = Env.Engine();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var result = await engine.RunAsync(inputs, Env.Offered(engine, "jpg", inputs), ct: cancel.Token);

        Assert.True(result.Canceled);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task Progress_is_reported_per_file()
    {
        using var scratch = new Scratch();
        var inputs = new[] { scratch.Image("1.png"), scratch.Image("2.png") };
        var engine = Env.Engine();
        var reports = new List<BatchProgress>();

        await engine.RunAsync(inputs, Env.Offered(engine, "bmp", inputs), new ActionProgress<BatchProgress>(reports.Add), TestContext.Current.CancellationToken);

        Assert.Contains(reports, r => r.Index == 0 && r.Current == "1.png");
        Assert.Contains(reports, r => r.Index == 1 && r.Current == "2.png");
        Assert.Equal(1.0, reports.Where(r => !double.IsNaN(r.Fraction)).Max(r => r.Fraction));
    }
}
