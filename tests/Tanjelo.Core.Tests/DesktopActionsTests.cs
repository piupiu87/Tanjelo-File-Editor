using Tanjelo.Core.Converters;

namespace Tanjelo.Core.Tests;

public class DesktopActionsTests
{
    sealed class FakeDesktop : IDesktopActions
    {
        public List<string> Shared { get; } = [];

        public Task ShareAsync(IReadOnlyList<string> files)
        {
            Shared.AddRange(files);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Share_is_offered_for_files_only()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("a.txt", "a");
        var folder = scratch.Folder("pics");

        Assert.Contains("share", Env.Keys(file));
        Assert.DoesNotContain("share", Env.Keys(file, folder));
        var share = Env.Engine().GetMenu([file]).Targets.Single(t => t.Key == "share");
        Assert.Equal((TargetKind.Action, true), (share.Kind, share.Quiet));
    }

    [Fact]
    public async Task The_app_shares_the_selection()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var desktop = new FakeDesktop();
        var engine = new ConverterEngine(Env.Tools, null, desktop);

        await Env.Run(engine, "share", a, b);

        Assert.Equal([a, b], desktop.Shared);
    }

    [Fact]
    public async Task Without_the_app_it_says_it_needs_it()
    {
        using var scratch = new Scratch();
        var file = scratch.Text("a.txt", "a");
        var engine = Env.Engine();

        var result = await engine.RunAsync([file], Env.Offered(engine, "share", [file]), ct: TestContext.Current.CancellationToken);

        Assert.StartsWith("Share needs the Tanjelo app", Assert.Single(result.Failures).Message);
    }
}
