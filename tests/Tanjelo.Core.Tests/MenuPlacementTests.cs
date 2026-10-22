namespace Tanjelo.Core.Tests;

public class MenuPlacementTests
{
    // A 1920 x 1200 screen with a 50-pixel taskbar at the bottom.
    static readonly Native.RECT Work = new() { Left = 0, Top = 0, Right = 1920, Bottom = 1150 };

    static Native.POINT Pointer(int x, int y) => new() { X = x, Y = y };

    [Fact]
    public void Opens_below_the_pointer_when_there_is_room()
    {
        var pointer = Pointer(300, 150);
        Assert.False(MenuPlacement.OpensAbove(pointer, Work, 827));
        Assert.Equal((302, 152), MenuPlacement.Position(pointer, Work, 413, 827, above: false));
    }

    [Fact]
    public void Opens_above_the_pointer_near_the_bottom_of_the_screen()
    {
        var pointer = Pointer(1100, 1017);
        Assert.True(MenuPlacement.OpensAbove(pointer, Work, 827));
        Assert.Equal((1102, 188), MenuPlacement.Position(pointer, Work, 413, 827, above: true));
    }

    [Theory]
    [InlineData(new[] { 827, 381, 827, 507 })] // Convert, Actions, Convert, Tools: the sequence that once ran off the screen
    [InlineData(new[] { 827, 255, 213, 827, 255 })] // typing to filter: "mute", "zzz", cleared, "2x"
    public void Above_the_pointer_the_bottom_edge_stays_put_as_the_height_changes(int[] heights)
    {
        var pointer = Pointer(1100, 1017);
        foreach (var height in heights)
        {
            var (_, y) = MenuPlacement.Position(pointer, Work, 413, height, above: true);
            Assert.Equal(1015, y + height);
        }
    }

    [Fact]
    public void Below_the_pointer_the_top_edge_stays_put_unless_the_menu_would_run_off_the_screen()
    {
        var pointer = Pointer(300, 300);
        Assert.Equal(302, MenuPlacement.Position(pointer, Work, 413, 381, above: false).Y);
        Assert.Equal(302, MenuPlacement.Position(pointer, Work, 413, 827, above: false).Y);
        // Taller than the room left below the pointer: it moves up just enough to stay on screen.
        Assert.Equal(150, MenuPlacement.Position(pointer, Work, 413, 1000, above: false).Y);
    }

    [Fact]
    public void Never_goes_above_the_top_of_the_work_area()
    {
        Assert.Equal(0, MenuPlacement.Position(Pointer(300, 600), Work, 413, 1100, above: true).Y);
    }

    [Fact]
    public void Flips_left_of_the_pointer_at_the_right_edge()
    {
        Assert.Equal(1800 - 413 - 2, MenuPlacement.Position(Pointer(1800, 100), Work, 413, 381, above: false).X);
    }
}
