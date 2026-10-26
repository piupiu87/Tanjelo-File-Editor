namespace Tanjelo.Core.Tests;

/// <summary>Tool options: how values are read and checked, remembered by the app, and previewed by the engine.</summary>
public class OptionsTests
{
    static readonly Option Percent = new("percent", "Percentage", OptionKind.Number, "50") { Min = 10, Max = 90, Whole = true };
    static readonly Option Mode = new("mode", "How", OptionKind.Choice, "percent") { Choices = [new("percent", "Percentage"), new("size", "Target size")] };

    static Target Tool(params Option[] options) => new Target("t", "Tool…", "", Section.Image) { Kind = TargetKind.Tool }.WithOptions(options);

    [Theory]
    [InlineData("75", 75)]
    [InlineData("1:15", 75)]
    [InlineData("1:15.5", 75.5)]
    [InlineData("01:02:03", 3723)]
    [InlineData(" 0.25 ", 0.25)]
    public void Times_are_read_as_seconds(string text, double seconds)
    {
        Assert.True(OptionValues.TryTime(text, out var value));
        Assert.Equal(seconds, value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1:75")]
    [InlineData("-5")]
    [InlineData("1::2")]
    [InlineData("1:2:3:4")]
    [InlineData("abc")]
    public void Bad_times_are_refused(string text) => Assert.False(OptionValues.TryTime(text, out _));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5.5, "0:05.5")]
    [InlineData(75.25, "1:15.25")]
    [InlineData(3723, "1:02:03")]
    [InlineData(59.9996, "1:00")]
    public void Times_are_written_as_minutes_and_seconds(double seconds, string text) => Assert.Equal(text, OptionValues.FormatTime(seconds));

    [Fact]
    public void Lists_of_times_come_back_sorted_without_repeats()
    {
        Assert.True(OptionValues.TryTimes("1:00, 0:05;0:05 30", out var times));
        Assert.Equal([5, 30, 60], times);
        Assert.Equal("0:05, 0:30, 1:00", OptionValues.FormatTimes(times));
        Assert.False(OptionValues.TryTimes("0:05, soon", out _));
    }

    [Theory]
    [InlineData("", 0, null)]
    [InlineData("0:30-1:45", 30, 105.0)]
    [InlineData("0:30-", 30, null)]
    [InlineData("-1:45", 0, 105.0)]
    public void Time_ranges_can_leave_out_either_end(string text, double start, double? end)
    {
        Assert.True(OptionValues.TryRange(text, out var range));
        Assert.Equal((start, end), range);
        Assert.Equal(text, OptionValues.FormatRange(range.Start, range.End));
    }

    [Theory]
    [InlineData("1:45-0:30")]
    [InlineData("0:30")]
    [InlineData("a-b")]
    public void Backwards_or_unreadable_ranges_are_refused(string text) => Assert.False(OptionValues.TryRange(text, out _));

    [Fact]
    public void Orders_must_name_each_file_once()
    {
        Assert.True(OptionValues.TryOrder("2,1,3", out var order));
        Assert.Equal([1, 0, 2], order);
        Assert.True(OptionValues.TryOrder("", out var none));
        Assert.Empty(none);
        Assert.False(OptionValues.TryOrder("1,1,2", out _));
        Assert.False(OptionValues.TryOrder("1,3", out _));
        Assert.False(OptionValues.TryOrder("0,1", out _));
    }

    [Theory]
    [InlineData("#FF8800", 255, 136, 0, 255)]
    [InlineData("ff8800", 255, 136, 0, 255)]
    [InlineData("#F80", 255, 136, 0, 255)]
    [InlineData("#FF880080", 255, 136, 0, 128)]
    [InlineData("transparent", 0, 0, 0, 0)]
    public void Colours_are_hex_with_optional_transparency(string text, byte r, byte g, byte b, byte a)
    {
        Assert.True(Colour.TryParse(text, out var colour));
        Assert.Equal(new Colour(r, g, b, a), colour);
    }

    [Fact]
    public void Colours_are_written_back_in_the_short_form_when_opaque()
    {
        Assert.Equal("#FF8800", new Colour(255, 136, 0).ToString());
        Assert.Equal("#FF880080", new Colour(255, 136, 0, 128).ToString());
        Assert.Equal("0xFF880080", new Colour(255, 136, 0, 128).ToFfmpeg());
        Assert.False(Colour.TryParse("orange", out _));
    }

    [Fact]
    public void Options_put_their_defaults_in_the_parameters()
    {
        var target = Tool(Mode, Percent);

        Assert.Equal("percent", target.Parameters["mode"]);
        Assert.Equal("50", target.Parameters["percent"]);
        Assert.Equal(50, target.WithParameters(new Dictionary<string, string>()).Number("percent"));
    }

    [Fact]
    public void Each_kind_checks_its_values()
    {
        Assert.Null(Percent.Problem("90"));
        Assert.Equal("must be a whole number from 10 to 90", Percent.Problem("95"));
        Assert.Equal("must be a whole number from 10 to 90", Percent.Problem("12.5"));
        Assert.NotNull(Mode.Problem("half"));
        Assert.NotNull(new Option("on", "On", OptionKind.Toggle, "true").Problem("maybe"));
        Assert.Equal("is needed", new Option("f", "Picture", OptionKind.File, "").Problem(""));
        Assert.Null(new Option("f", "Picture", OptionKind.File, "") { Optional = true }.Problem(""));
        Assert.Equal("must be a file that exists", new Option("f", "Picture", OptionKind.File, "").Problem(@"C:\no\such\file.png"));
        Assert.Equal("needs at least one time", new Option("t", "Times", OptionKind.Times, "").Problem(""));
        Assert.Null(new Option("t", "Times", OptionKind.Times, "") { Optional = true }.Problem(""));
        Assert.Equal("is needed", new Option("x", "Title", OptionKind.Text, "").Problem(" "));
    }

    [Fact]
    public void Bad_values_stop_the_tool_with_a_message_naming_the_setting()
    {
        var target = Tool(Mode, Percent).WithParameters(new Dictionary<string, string> { ["percent"] = "200" });

        var error = Assert.Throws<ConversionException>(() => target.Number("percent"));
        Assert.Equal("Percentage must be a whole number from 10 to 90, not '200'.", error.Message);
        Assert.Throws<InvalidOperationException>(() => target.Choice("percent")); // asked for the wrong kind: a bug, not a user error
    }

    [Fact]
    public void Options_can_depend_on_another_option()
    {
        var size = new Option("size", "Target size", OptionKind.Number, "1") { Min = 0.1, Max = 100, ShownWhen = new Condition("mode", "size") };

        Assert.False(size.IsShown(Tool(Mode, size).Parameters));
        Assert.True(size.IsShown(new Dictionary<string, string> { ["mode"] = "size" }));

        var either = size with { ShownWhen = new Condition("mode", "size") { Or = new Condition("fit", "exact") } };
        Assert.True(either.IsShown(new Dictionary<string, string> { ["mode"] = "percent", ["fit"] = "exact" }));
        Assert.False(either.IsShown(new Dictionary<string, string> { ["mode"] = "percent", ["fit"] = "loose" }));
    }

    [Fact]
    public void An_order_rearranges_the_selection_and_must_match_its_length()
    {
        var order = new Option("order", "Order", OptionKind.Order, "");
        string[] files = ["a.mp4", "b.mp4", "c.mp4"];

        Assert.Equal(files, Tool(order).Ordered("order", files));
        Assert.Equal(["c.mp4", "a.mp4", "b.mp4"], Tool(order).WithParameters(new Dictionary<string, string> { ["order"] = "3,1,2" }).Ordered("order", files));
        Assert.Throws<ConversionException>(() => Tool(order).WithParameters(new Dictionary<string, string> { ["order"] = "2,1" }).Ordered("order", files));
    }

    [Fact]
    public void The_app_remembers_valid_values_of_options_that_ask_for_it()
    {
        using var scratch = new Scratch();
        var times = new Option("times", "Times", OptionKind.Times, "") { Remember = false };
        var target = Tool(Mode, Percent, times);
        var memory = new ToolMemory(scratch.PathOf("tools.json"));

        Assert.Empty(memory.Recall(target));
        memory.Store(target.WithParameters(new Dictionary<string, string> { ["mode"] = "size", ["percent"] = "30", ["times"] = "0:05" }));

        var recalled = new ToolMemory(scratch.PathOf("tools.json")).Recall(target);
        Assert.Equal(new Dictionary<string, string> { ["mode"] = "size", ["percent"] = "30" }, recalled);

        // A newer version of the tool with a narrower range drops the value that no longer fits.
        var narrower = Tool(Mode, Percent with { Max = 25 });
        Assert.Equal(["mode"], memory.Recall(narrower).Keys);
    }

    [Fact]
    public void A_damaged_memory_file_just_means_starting_from_the_defaults()
    {
        using var scratch = new Scratch();
        var path = scratch.Text("tools.json", "{ not json");
        var memory = new ToolMemory(path);

        Assert.Empty(memory.Recall(Tool(Mode)));
        memory.Store(Tool(Mode));
        Assert.Equal("percent", new ToolMemory(path).Recall(Tool(Mode))["mode"]);
    }

    sealed class Shrink : IConverter, IPreviewer
    {
        public IEnumerable<Target> TargetsFor(string input) => [Tool(Mode, Percent) with { Key = "shrink" }];

        public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<Preview> PreviewAsync(IReadOnlyList<string> inputs, Target target, PreviewRequest request, JobContext ctx) =>
            Task.FromResult(new Preview(null, [new Fact("Percentage", OptionValues.Format(target.Number("percent"))), new Fact("Of", inputs[0])]));
    }

    sealed class Plain : IConverter
    {
        public IEnumerable<Target> TargetsFor(string input) => [new Target("plain", "Plain", "", Section.Image) { Kind = TargetKind.Tool }];

        public Task<IReadOnlyList<string>> ConvertAsync(string input, Target target, JobContext ctx) => Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public async Task Previews_get_the_values_being_edited_and_check_them()
    {
        var engine = new ConverterEngine(new Toolbox(), null, [new Shrink(), new Plain()], []);
        string[] files = [@"C:\a.png", @"C:\b.png"];
        var shrink = Env.Offered(engine, "shrink", files);
        var ct = TestContext.Current.CancellationToken;

        Assert.True(engine.CanPreview(files, shrink));
        Assert.False(engine.CanPreview(files, Env.Offered(engine, "plain", files)));
        Assert.Null(await engine.PreviewAsync(files, Env.Offered(engine, "plain", files), new PreviewRequest(), ct));

        var preview = await engine.PreviewAsync(files, shrink.WithParameters(new Dictionary<string, string> { ["percent"] = "20" }), new PreviewRequest(), ct);
        Assert.Equal([new Fact("Percentage", "20"), new Fact("Of", @"C:\a.png")], preview!.Facts);

        await Assert.ThrowsAsync<ConversionException>(() =>
            engine.PreviewAsync(files, shrink.WithParameters(new Dictionary<string, string> { ["percent"] = "5" }), new PreviewRequest(), ct));
    }
}
