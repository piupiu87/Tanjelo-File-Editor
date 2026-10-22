namespace Tanjelo.Core.Tests;

public class MenuSearchTests
{
    static readonly Target Mute = new("mute", "Mute", "Remove the sound, losslessly", Section.Video) { Kind = TargetKind.Tool };
    static readonly Target MoveTo = new("move-to", "Move to…", "Pick a folder", Section.File) { Kind = TargetKind.Action };
    static readonly Target Mov = new("mov", "MOV", "QuickTime movie", Section.Video);
    static readonly TargetKind[] Tabs = [TargetKind.Convert, TargetKind.Tool, TargetKind.Action];

    [Theory]
    [InlineData("move", "move-to", MenuSearch.NameMatch)]   // in the label
    [InlineData("MOVE", "move-to", MenuSearch.NameMatch)]   // any case
    [InlineData("move-", "move-to", MenuSearch.NameMatch)]  // in the key
    [InlineData("move", "mute", MenuSearch.DescriptionMatch)]
    [InlineData("folder", "move-to", MenuSearch.DescriptionMatch)]
    [InlineData("zzz", "mute", MenuSearch.NoMatch)]
    [InlineData("  ", "mute", MenuSearch.NameMatch)]        // an empty filter shows everything
    public void Names_and_keys_score_above_descriptions(string query, string key, int expected)
    {
        var target = new[] { Mute, MoveTo }.Single(t => t.Key == key);
        Assert.Equal(expected, MenuSearch.Score(target, query));
    }

    [Fact]
    public void Extra_text_such_as_the_group_counts_like_the_description()
    {
        Assert.Equal(MenuSearch.DescriptionMatch, MenuSearch.Score(Mute, "video", extra: "Video"));
    }

    [Theory]
    [InlineData("move", TargetKind.Convert, TargetKind.Action)] // the name match on Actions beats Mute's description on Tools
    [InlineData("mute", TargetKind.Convert, TargetKind.Tool)]
    [InlineData("mov", TargetKind.Convert, TargetKind.Convert)] // MOV is on Convert: stay
    [InlineData("mov", TargetKind.Action, TargetKind.Action)]   // "Move to…" matches by name too, so the current tab is as good
    [InlineData("remove", TargetKind.Convert, TargetKind.Tool)] // only a description match anywhere: go to it
    public void The_tab_with_the_best_match_wins(string query, TargetKind current, TargetKind expected)
    {
        var scores = new[] { Mov, Mute, MoveTo }.Select(t => (t.Kind, MenuSearch.Score(t, query)));
        Assert.Equal(expected, MenuSearch.BestTab(scores, current, Tabs));
    }

    [Fact]
    public void Nothing_matching_anywhere_gives_no_tab()
    {
        var scores = new[] { Mov, Mute, MoveTo }.Select(t => (t.Kind, MenuSearch.Score(t, "zzz")));
        Assert.Null(MenuSearch.BestTab(scores, TargetKind.Tool, Tabs));
    }

    [Fact]
    public void Typing_move_for_a_video_finds_move_to_not_mute()
    {
        Assert.SkipUnless(Env.HasFfmpeg, Env.NoFfmpeg);
        using var scratch = new Scratch();
        var menu = Env.Engine().GetMenu([scratch.Text("talk.mp4", "")]);

        var scores = menu.Targets.Select(t => (t.Kind, MenuSearch.Score(t, "move"))).ToList();

        Assert.Contains(scores, s => s.Kind == TargetKind.Tool && s.Item2 == MenuSearch.DescriptionMatch); // Mute: "Remove the sound"
        Assert.Equal(TargetKind.Action, MenuSearch.BestTab(scores, TargetKind.Convert, Tabs));
    }
}
