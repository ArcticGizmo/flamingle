using Flamingle.Core.Geometry;
using Flamingle.Core.Zones;

namespace Flamingle.Tests;

public class ZoneDividerTests
{
    private static IReadOnlyList<ZoneFraction> Zones(string id) => ZoneLayout.ById(id).Zones;

    [Fact]
    public void Two_columns_have_one_vertical_divider()
    {
        var d = Assert.Single(ZoneDividers.Find(Zones("columns-2")));
        Assert.True(d.Vertical);
        Assert.Equal(0.5, d.Position, 6);
        Assert.Equal([0], d.Before);
        Assert.Equal([1], d.After);
    }

    [Fact]
    public void Grid_dividers_span_the_whole_grid()
    {
        var dividers = ZoneDividers.Find(Zones("grid-2x2"));
        Assert.Equal(2, dividers.Count);

        var v = dividers.Single(d => d.Vertical);
        Assert.Equal([0, 2], v.Before);   // both rows' left zones
        Assert.Equal([1, 3], v.After);
        Assert.Equal((0.0, 1.0), (v.Start, v.End));

        var h = dividers.Single(d => !d.Vertical);
        Assert.Equal([0, 1], h.Before);
        Assert.Equal([2, 3], h.After);
    }

    [Fact]
    public void A_stack_divider_only_spans_the_stack()
    {
        var dividers = ZoneDividers.Find(Zones("main-stack"));
        var h = dividers.Single(d => !d.Vertical);
        Assert.Equal([1], h.Before);
        Assert.Equal([2], h.After);
        Assert.Equal(0.6, h.Start, 6);
        Assert.Equal(1.0, h.End, 6);

        var v = dividers.Single(d => d.Vertical);
        Assert.Equal([0], v.Before);
        Assert.Equal([1, 2], v.After);
    }

    [Fact]
    public void Separate_runs_on_the_same_line_are_separate_dividers()
    {
        // Wide centre: the left and right stacks each split at y = 0.5, but the centre zone breaks the line.
        var horizontal = ZoneDividers.Find(Zones("wide-centre")).Where(d => !d.Vertical).ToList();
        Assert.Equal(2, horizontal.Count);
        Assert.All(horizontal, d => Assert.Equal(0.5, d.Position, 6));
    }

    [Fact]
    public void A_single_zone_has_no_dividers()
        => Assert.Empty(ZoneDividers.Find(Zones("focus")));

    [Fact]
    public void Moving_a_divider_resizes_both_sides()
    {
        var zones = Zones("grid-2x2");
        var v = ZoneDividers.Find(zones).Single(d => d.Vertical);

        var moved = ZoneDividers.Move(zones, v, 0.7, minSize: 0.1);
        Assert.Equal(0.7, moved[0].Width, 6);
        Assert.Equal(0.7, moved[2].Width, 6);
        Assert.Equal(0.7, moved[1].X, 6);
        Assert.Equal(0.3, moved[1].Width, 6);
        Assert.Equal(zones[1].Height, moved[1].Height);   // the other axis is untouched
    }

    [Fact]
    public void Moving_a_divider_respects_the_minimum_size()
    {
        var zones = Zones("columns-3");
        var first = ZoneDividers.Find(zones).OrderBy(d => d.Position).First();

        var moved = ZoneDividers.Move(zones, first, 0.9, minSize: 0.1);
        // Can't pass the next column's far edge minus the minimum: 2/3 − 0.1.
        Assert.Equal(2.0 / 3 - 0.1, moved[1].X, 6);
        Assert.Equal(0.1, moved[1].Width, 6);
    }

    [Fact]
    public void For_edge_finds_the_divider_a_window_resize_is_dragging()
    {
        var zones = Zones("main-stack");   // 0 = main (left), 1 = stack top, 2 = stack bottom

        var right = ZoneDividers.ForEdge(zones, [0], ZoneDividers.Edge.Right);
        Assert.NotNull(right);
        Assert.True(right.Vertical);

        var left = ZoneDividers.ForEdge(zones, [1], ZoneDividers.Edge.Left);
        Assert.Equal(right.Position, left!.Position, 6);   // the same line, from the other side

        var below = ZoneDividers.ForEdge(zones, [1], ZoneDividers.Edge.Bottom);
        Assert.False(below!.Vertical);

        Assert.Null(ZoneDividers.ForEdge(zones, [0], ZoneDividers.Edge.Left));   // the screen edge
        Assert.Null(ZoneDividers.ForEdge(zones, [0], ZoneDividers.Edge.Bottom));
    }

    [Fact]
    public void For_edge_works_for_a_window_spanning_zones()
    {
        var zones = Zones("columns-3");
        var d = ZoneDividers.ForEdge(zones, [0, 1], ZoneDividers.Edge.Right);
        Assert.Equal(2.0 / 3, d!.Position, 6);
    }

    [Fact]
    public void Handle_rect_covers_the_gap_and_is_thickened_when_there_is_none()
    {
        var zones = Zones("columns-2");
        var d = Assert.Single(ZoneDividers.Find(zones));
        var work = new PixelRect(0, 0, 1000, 500);

        var spaced = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), work, spacing: 10);
        Assert.Equal(PixelRect.FromEdges(495, 10, 505, 490), ZoneDividers.HandleRect(d, spaced, minThickness: 8));

        var flush = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), work, spacing: 0);
        Assert.Equal(PixelRect.FromEdges(496, 0, 504, 500), ZoneDividers.HandleRect(d, flush, minThickness: 8));
    }
}
