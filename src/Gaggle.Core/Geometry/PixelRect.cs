namespace Gaggle.Core.Geometry;

/// <summary>A rectangle in physical (per-monitor-DPI-aware) screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);

    public static PixelRect FromEdges(int left, int top, int right, int bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>The overlapping area of two rectangles (0 when they don't overlap).</summary>
    public long IntersectionArea(PixelRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w > 0 && h > 0 ? (long)w * h : 0;
    }

    /// <summary>True when every edge is within <paramref name="tolerance"/> pixels of the other's.</summary>
    public bool RoughlyEquals(PixelRect other, int tolerance)
        => Math.Abs(X - other.X) <= tolerance && Math.Abs(Y - other.Y) <= tolerance
        && Math.Abs(Right - other.Right) <= tolerance && Math.Abs(Bottom - other.Bottom) <= tolerance;
}
