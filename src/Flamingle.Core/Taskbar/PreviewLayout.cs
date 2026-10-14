using Flamingle.Core.Geometry;

namespace Flamingle.Core.Taskbar;

/// <summary>
/// Lays a flock's windows out for its taskbar preview: the windows' screen rectangles, shrunk together (keeping
/// their arrangement and proportions) to fit the size the taskbar asks for — like a Snap Group's preview.
/// </summary>
public static class PreviewLayout
{
    /// <summary>The preview's size and each window's rectangle in it (same order as <paramref name="windows"/>).
    /// Empty / degenerate input gives a 0×0 preview.</summary>
    public static (int Width, int Height, IReadOnlyList<PixelRect> Tiles) Fit(
        IReadOnlyList<PixelRect> windows, int maxWidth, int maxHeight)
    {
        var real = windows.Where(w => w.Width > 0 && w.Height > 0).ToList();
        if (real.Count == 0 || maxWidth <= 0 || maxHeight <= 0)
            return (0, 0, windows.Select(_ => default(PixelRect)).ToList());

        var left = real.Min(w => w.X);
        var top = real.Min(w => w.Y);
        var right = real.Max(w => w.Right);
        var bottom = real.Max(w => w.Bottom);

        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)(right - left), maxHeight / (double)(bottom - top)));
        var width = Math.Clamp((int)Math.Round((right - left) * scale), 1, maxWidth);
        var height = Math.Clamp((int)Math.Round((bottom - top) * scale), 1, maxHeight);

        var tiles = windows.Select(w => w.Width <= 0 || w.Height <= 0
                ? default
                : PixelRect.FromEdges(
                    (int)Math.Round((w.X - left) * scale),
                    (int)Math.Round((w.Y - top) * scale),
                    (int)Math.Round((w.Right - left) * scale),
                    (int)Math.Round((w.Bottom - top) * scale)))
            .ToList();
        return (width, height, tiles);
    }
}
