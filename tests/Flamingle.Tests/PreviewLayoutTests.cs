using Flamingle.Core.Geometry;
using Flamingle.Core.Taskbar;

namespace Flamingle.Tests;

public class PreviewLayoutTests
{
    [Fact]
    public void Side_by_side_windows_shrink_together_keeping_the_arrangement()
    {
        // Two halves of a 2000×1000 area, offset on screen.
        var (w, h, tiles) = PreviewLayout.Fit(
            [new PixelRect(100, 50, 1000, 1000), new PixelRect(1100, 50, 1000, 1000)], 200, 200);

        Assert.Equal((200, 100), (w, h));
        Assert.Equal(new PixelRect(0, 0, 100, 100), tiles[0]);
        Assert.Equal(new PixelRect(100, 0, 100, 100), tiles[1]);
    }

    [Fact]
    public void Tall_layouts_are_limited_by_height() =>
        Assert.Equal((50, 100), Dims(PreviewLayout.Fit([new PixelRect(0, 0, 500, 1000)], 200, 100)));

    [Fact]
    public void Small_layouts_are_never_enlarged() =>
        Assert.Equal((120, 80), Dims(PreviewLayout.Fit([new PixelRect(0, 0, 120, 80)], 200, 200)));

    [Fact]
    public void Empty_rectangles_get_an_empty_tile_but_keep_their_place()
    {
        var (_, _, tiles) = PreviewLayout.Fit([default, new PixelRect(0, 0, 100, 100)], 50, 50);
        Assert.Equal(default, tiles[0]);
        Assert.Equal(new PixelRect(0, 0, 50, 50), tiles[1]);
    }

    [Fact]
    public void Nothing_to_show_gives_an_empty_preview() =>
        Assert.Equal((0, 0), Dims(PreviewLayout.Fit([default], 200, 200)));

    private static (int, int) Dims((int Width, int Height, IReadOnlyList<PixelRect> _) fit) => (fit.Width, fit.Height);
}
