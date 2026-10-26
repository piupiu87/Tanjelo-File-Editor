namespace Tanjelo.Core.Tests;

public class ShelfTests
{
    [Fact]
    public void Files_and_folders_are_added_once_in_order()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var folder = scratch.Folder("pics", "1.png");
        var shelf = new Shelf();

        var first = shelf.Add([a, folder]);
        var second = shelf.Add([a.ToUpperInvariant(), b, folder + "\\", scratch.PathOf("missing.txt")]);

        Assert.Equal([a, folder], first);
        Assert.Equal([b], second);
        Assert.Equal([a, folder, b], shelf.Items);
    }

    [Fact]
    public void Items_whose_files_are_gone_drop_off()
    {
        using var scratch = new Scratch();
        var keep = scratch.Text("keep.txt", "k");
        var moved = scratch.Text("moved.txt", "m");
        var shelf = new Shelf();
        shelf.Add([keep, moved]);
        var changes = 0;
        shelf.Changed += () => changes++;

        File.Move(moved, scratch.PathOf("elsewhere.txt"));
        var pruned = shelf.Prune();

        Assert.Equal([moved], pruned);
        Assert.Equal([keep], shelf.Items);
        Assert.Equal(1, changes);
        Assert.Empty(shelf.Prune());
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Items_can_be_removed_or_cleared()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var shelf = new Shelf();
        shelf.Add([a, b]);

        Assert.True(shelf.Remove([a.ToUpperInvariant()]));
        Assert.False(shelf.Remove([a]));
        Assert.Equal([b], shelf.Items);
        shelf.Clear();
        Assert.Empty(shelf.Items);
    }

    [Fact]
    public void The_shelf_survives_a_restart_without_what_was_deleted()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var b = scratch.Text("b.txt", "b");
        var file = scratch.PathOf(@"settings\float.json");
        var shelf = new Shelf { Open = true };
        shelf.Add([a, b]);
        shelf.Save(file);

        File.Delete(b);
        var loaded = Shelf.Load(file);

        Assert.Equal([a], loaded.Items);
        Assert.True(loaded.Open);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json at all")]
    [InlineData("{\"items\": null}")]
    public void A_missing_or_damaged_file_is_an_empty_shelf(string? content)
    {
        using var scratch = new Scratch();
        var file = scratch.PathOf("float.json");
        if (content is not null) File.WriteAllText(file, content);

        var shelf = Shelf.Load(file);

        Assert.Empty(shelf.Items);
        Assert.False(shelf.Open);
    }

    [Fact]
    public void An_empty_shelf_doesn_t_reopen()
    {
        using var scratch = new Scratch();
        var a = scratch.Text("a.txt", "a");
        var file = scratch.PathOf("float.json");
        var shelf = new Shelf { Open = true };
        shelf.Add([a]);
        shelf.Save(file);
        File.Delete(a);

        Assert.False(Shelf.Load(file).Open);
    }
}
