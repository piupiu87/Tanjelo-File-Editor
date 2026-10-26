using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class DesktopActionsTests
{
    sealed class FakeDesktop : IDesktopActions
    {
        public List<string> Shared { get; } = [];
        public List<string> Floated { get; } = [];

        public Task ShareAsync(IReadOnlyList<string> files)
        {
            Shared.AddRange(files);
            return Task.CompletedTask;
        }

        public Task FloatAsync(IReadOnlyList<string> paths)
        {
            Floated.AddRange(paths);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Share_is_offered_for_files_only_and_float_for_anything()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("a.txt", "a");
        var folder = scratch.Folder("pics");

        Assert.Contains("share", Env.Keys(file));
        Assert.Contains("float", Env.Keys(file));
        Assert.DoesNotContain("share", Env.Keys(file, folder));
        Assert.Contains("float", Env.Keys(file, folder));
        var share = Env.Engine().GetMenu([file]).Targets.Single(t => t.Key == "share");
        Assert.Equal((TargetKind.Action, true), (share.Kind, share.Quiet));
    }

    [Fact]
    public async Task The_app_shares_and_floats_the_selection()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var desktop = new FakeDesktop();
        var engine = new ConverterEngine(Env.Tools, null, desktop);

        await Env.Run(engine, "share", a, b);
        await Env.Run(engine, "float", a);

        Assert.Equal([a, b], desktop.Shared);
        Assert.Equal([a], desktop.Floated);
    }

    [Fact]
    public async Task Without_the_app_they_say_they_need_it()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("a.txt", "a");
        var engine = Env.Engine();

        var shared = await engine.RunAsync([file], Env.Offered(engine, "share", [file]), ct: TestContext.Current.CancellationToken);
        var floated = await engine.RunAsync([file], Env.Offered(engine, "float", [file]), ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("Share needs the Tanjelo app", Assert.Single(shared.Failures).Message);
        Assert.StartsWith("Float needs the Tanjelo app", Assert.Single(floated.Failures).Message);
    }
}
