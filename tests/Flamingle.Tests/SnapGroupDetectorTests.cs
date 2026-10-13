using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Windows;

namespace Flamingle.Tests;

public class SnapGroupDetectorTests
{
    private const long MonA = 1;
    private const long MonB = 2;

    private static WindowSnapshot W(long handle, int x, int y, int w, int h,
        long monitor = MonA, bool min = false, bool max = false)
        => new(handle, 100, $"app{handle}", $"Window {handle}", new PixelRect(x, y, w, h), min, max, monitor);

    [Fact]
    public void Two_halves_are_one_group()
    {
        var windows = new[] { W(1, 0, 0, 960, 1040), W(2, 960, 0, 960, 1040) };
        Assert.Equal([1L, 2L], SnapGroupDetector.Detect(1, windows));
    }

    [Fact]
    public void Quarters_chain_together_through_shared_edges()
    {
        var windows = new[]
        {
            W(1, 0, 0, 960, 520), W(2, 960, 0, 960, 520),
            W(3, 0, 520, 960, 520), W(4, 960, 520, 960, 520),
        };
        Assert.Equal([1L, 2L, 3L, 4L], SnapGroupDetector.Detect(4, windows));
    }

    [Fact]
    public void Small_gaps_between_tiles_are_tolerated()
    {
        var windows = new[] { W(1, 0, 0, 955, 1040), W(2, 965, 0, 955, 1040) };
        Assert.Equal([1L, 2L], SnapGroupDetector.Detect(1, windows));
    }

    [Fact]
    public void A_floating_window_on_top_of_the_layout_is_excluded()
    {
        var windows = new[]
        {
            W(9, 700, 300, 600, 400),               // floating, overlaps both halves
            W(1, 0, 0, 960, 1040), W(2, 960, 0, 960, 1040),
        };
        Assert.Equal([1L, 2L], SnapGroupDetector.Detect(1, windows));
    }

    [Fact]
    public void Windows_on_another_monitor_are_excluded_even_if_they_touch()
    {
        var windows = new[] { W(1, 0, 0, 1920, 1040), W(2, 1920, 0, 1920, 1040, monitor: MonB) };
        Assert.Equal([1L], SnapGroupDetector.Detect(1, windows));
    }

    [Fact]
    public void Minimised_and_maximised_windows_are_not_neighbours()
    {
        var windows = new[]
        {
            W(1, 0, 0, 960, 1040),
            W(2, 960, 0, 960, 1040, min: true),
            W(3, 960, 0, 960, 1040, max: true),
        };
        Assert.Equal([1L], SnapGroupDetector.Detect(1, windows));
    }

    [Fact]
    public void A_maximised_seed_is_a_group_of_one()
    {
        var windows = new[] { W(1, 0, 0, 1920, 1040, max: true), W(2, 1920, 0, 100, 100) };
        Assert.Equal([1L], SnapGroupDetector.Detect(1, windows));
    }

    [Fact]
    public void Corner_contact_alone_is_not_enough()
    {
        // Touch only at a corner: no shared edge length.
        var windows = new[] { W(1, 0, 0, 500, 500), W(2, 500, 500, 500, 500) };
        Assert.Equal([1L], SnapGroupDetector.Detect(1, windows));
    }

    [Fact]
    public void Unknown_seed_gives_nothing()
        => Assert.Empty(SnapGroupDetector.Detect(42, [W(1, 0, 0, 10, 10)]));

    [Fact]
    public void Result_keeps_the_input_z_order()
    {
        var windows = new[] { W(2, 960, 0, 960, 1040), W(1, 0, 0, 960, 1040) };
        Assert.Equal([2L, 1L], SnapGroupDetector.Detect(1, windows));
    }
}
