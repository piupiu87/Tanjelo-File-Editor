using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;

namespace Tanjelo.Core.Tests;

/// <summary>Dragging a crop box on the preview, in the picture's own pixels: a 400 × 300 picture with a box at (100, 50), 200 × 100.</summary>
public class BoxDragTests
{
    static readonly Size Picture = new(400, 300);
    static readonly Rect Box = new(100, 50, 200, 100);

    static Rect Drag(Point from, Point to, double? ratio = null) => BoxDrag.Start(from, Box, Picture, ratio, tolerance: 5).To(to);

    [Fact]
    public void Inside_the_box_moves_it_and_it_stays_on_the_picture()
    {
        Assert.Equal(new Rect(130, 70, 200, 100), Drag(new(200, 100), new(230, 120)));
        Assert.Equal(new Rect(200, 200, 200, 100), Drag(new(200, 100), new(900, 900)));
        Assert.Equal(BoxDrag.Kind.Move, BoxDrag.Start(new(200, 100), Box, Picture, null, 5).What);
    }

    [Fact]
    public void An_edge_moves_on_its_own_and_a_corner_moves_two()
    {
        Assert.Equal(new Rect(100, 50, 250, 100), Drag(new(300, 100), new(350, 100)));            // right edge
        Assert.Equal(new Rect(60, 20, 240, 130), Drag(new(100, 50), new(60, 20)));               // top-left corner
        Assert.Equal(new Rect(100, 50, 300, 250), Drag(new(300, 150), new(1000, 1000)));         // bottom-right, held at the picture's edge
    }

    [Fact]
    public void An_edge_can_not_pass_the_opposite_one()
    {
        Assert.Equal(new Rect(298, 50, 2, 100), Drag(new(100, 100), new(390, 100)));
    }

    [Fact]
    public void Outside_the_box_draws_a_new_one_in_any_direction()
    {
        Assert.Equal(new Rect(10, 200, 50, 80), Drag(new(60, 200), new(10, 280)));
        Assert.Equal(BoxDrag.Kind.Draw, BoxDrag.Start(new(20, 20), Box, Picture, null, 5).What);
    }

    [Fact]
    public void With_a_shape_to_keep_only_corners_resize_and_the_opposite_corner_stays()
    {
        Assert.Equal(BoxDrag.Kind.Move, BoxDrag.Start(new(300, 100), Box, Picture, 2, 5).What); // an edge just moves the box
        var bigger = Drag(new(300, 150), new(340, 160), ratio: 2);
        Assert.Equal(new Rect(100, 50, 240, 120), bigger);
        var held = Drag(new(300, 150), new(1000, 1000), ratio: 2);                            // as big as the picture allows
        Assert.Equal(new Rect(100, 50, 300, 150), held);
    }

    [Fact]
    public void A_new_box_with_a_shape_keeps_it_and_stays_on_the_picture()
    {
        Assert.Equal(new Rect(20, 20, 160, 160), Drag(new(20, 20), new(180, 60), ratio: 1));
        Assert.Equal(new Rect(300, 200, 100, 100), Drag(new(300, 200), new(999, 999), ratio: 1));
    }

    [Theory]
    [InlineData(100, 50, (int)(BoxDrag.Edges.Left | BoxDrag.Edges.Top))]
    [InlineData(302, 148, (int)(BoxDrag.Edges.Right | BoxDrag.Edges.Bottom))]
    [InlineData(200, 50, (int)BoxDrag.Edges.Top)]
    [InlineData(200, 100, (int)BoxDrag.Edges.None)]
    [InlineData(20, 20, (int)BoxDrag.Edges.None)]
    public void Points_near_an_edge_count_as_on_it(double x, double y, int edges) // the enum is internal to the app
        => Assert.Equal((BoxDrag.Edges)edges, BoxDrag.Hit(new Point(x, y), Box, 5, cornersOnly: false));
}
