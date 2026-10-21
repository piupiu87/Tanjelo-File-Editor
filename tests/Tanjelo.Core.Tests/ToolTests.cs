namespace Tanjelo.Core.Tests;

/// <summary>How the engine treats tools and actions, using stand-ins so no real converter is involved.</summary>
public class ToolTests
{
    /// <summary>A per-file tool for .txt files whose key happens to be "txt", like a "rewrap" that keeps the format.</summary>
    sealed class Rewrap : IConverter
    {
        public List<Target> Ran { get; } = [];

        public IEnumerable<Target> TargetsFor(string input) => Formats.Key(input) == "txt"
            ? [new Target("txt", "Rewrap", "Same format, changed copy", Section.Document)
            {
                Kind = TargetKind.Tool,
                Parameters = new Dictionary<string, string> { ["width"] = "80", ["indent"] = "0" },
            }]
            : [];

        public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx)
        {
            Ran.Add(target);
            return Task.FromResult<IReadOnlyList<string>>([input + ".out"]);
        }
    }

    sealed class CountItems : IBatchOperation
    {
        public List<Target> Ran { get; } = [];

        public IEnumerable<Target> TargetsFor(IReadOnlyList<string> inputs) =>
            [new Target("count", "Count", "", Section.Document)
            {
                IsBatch = true,
                Kind = TargetKind.Action,
                Parameters = new Dictionary<string, string> { ["unit"] = "items" },
            }];

        public Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<string> inputs, Target target, JobContext ctx)
        {
            Ran.Add(target);
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    readonly Rewrap _rewrap = new();
    readonly CountItems _count = new();

    ConverterEngine Engine() => new(new Toolbox(), null, [_rewrap], [_count]);

    [Fact]
    public async Task A_tool_runs_on_files_already_in_its_key_format()
    {
        var engine = Engine();
        var target = Env.Offered(engine, "txt", [@"C:\a.txt"]);

        var result = await engine.RunAsync([@"C:\a.txt"], target, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result.Skipped);
        Assert.Equal([@"C:\a.txt.out"], result.Outputs);
    }

    [Fact]
    public void A_tool_is_only_offered_when_every_file_supports_it()
    {
        var keys = Engine().GetMenu([@"C:\a.txt", @"C:\b.png"]).Targets.Select(t => t.Key);
        Assert.DoesNotContain("txt", keys);
        Assert.Contains("count", keys);
    }

    [Fact]
    public async Task Overridden_parameters_reach_the_tool_and_defaults_fill_the_rest()
    {
        var engine = Engine();
        var target = Env.Offered(engine, "txt", [@"C:\a.txt"]).WithParameters(new Dictionary<string, string> { ["width"] = "40" });

        await engine.RunAsync([@"C:\a.txt"], target, ct: TestContext.Current.CancellationToken);

        var ran = Assert.Single(_rewrap.Ran);
        Assert.Equal("40", ran.Parameters["width"]);
        Assert.Equal("0", ran.Parameters["indent"]);
    }

    [Fact]
    public async Task Overridden_parameters_reach_batch_operations()
    {
        var engine = Engine();
        var target = Env.Offered(engine, "count", [@"C:\a.txt"]).WithParameters(new Dictionary<string, string> { ["unit"] = "files" });

        await engine.RunAsync([@"C:\a.txt"], target, ct: TestContext.Current.CancellationToken);

        Assert.Equal("files", Assert.Single(_count.Ran).Parameters["unit"]);
    }

    [Fact]
    public void Number_parameters_are_validated()
    {
        var target = new Target("t", "Speed", "", Section.Video) { Parameters = new Dictionary<string, string> { ["factor"] = "2.5" } };

        Assert.Equal(2.5, target.Number("factor", 0.5, 4));
        var tooFast = Assert.Throws<ConversionException>(() => target.Number("factor", 0.5, 2));
        Assert.Contains("from 0.5 to 2", tooFast.Message);
        Assert.Throws<ConversionException>(() => target.Parameter("missing"));
    }

    [Fact]
    public void Conversions_and_tools_describe_themselves_for_progress_cards()
    {
        var png = new Target("png", "PNG", "", Section.Image);
        Assert.Equal("Converting to PNG…", png.RunningText);
        Assert.Equal("Converted to PNG", png.DoneText);

        var mute = png with { Running = "Muting…", Done = "Muted" };
        Assert.Equal("Muting…", mute.RunningText);
        Assert.Equal("Muted", mute.DoneText);
    }
}
