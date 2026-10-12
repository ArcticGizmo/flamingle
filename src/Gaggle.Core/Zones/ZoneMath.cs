using Gaggle.Core.Geometry;

namespace Gaggle.Core.Zones;

/// <summary>Pure zone geometry: resolving a layout onto a monitor, hit-testing, and spanning.</summary>
public static class ZoneMath
{
    /// <summary>
    /// Resolve <paramref name="layout"/> onto a monitor's work area. <paramref name="spacing"/> px separates
    /// neighbouring zones and insets the outer edge, so windows don't sit flush against each other.
    /// Fractional edges are rounded once, from the work area's origin, so neighbours share exact edges.
    /// </summary>
    public static IReadOnlyList<PixelRect> Resolve(ZoneLayout layout, PixelRect workArea, int spacing)
    {
        var half = spacing / 2.0;
        var list = new List<PixelRect>(layout.Zones.Count);
        foreach (var z in layout.Zones)
        {
            int Edge(double origin, double size, double fraction) => (int)Math.Round(origin + size * fraction);

            var left = Edge(workArea.X, workArea.Width, z.X);
            var top = Edge(workArea.Y, workArea.Height, z.Y);
            var right = Edge(workArea.X, workArea.Width, z.X + z.Width);
            var bottom = Edge(workArea.Y, workArea.Height, z.Y + z.Height);

            // Outer edges get the full spacing; inner edges half each (so the gap between zones = spacing).
            left += (int)Math.Round(left == workArea.X ? spacing : half);
            top += (int)Math.Round(top == workArea.Y ? spacing : half);
            right -= (int)Math.Round(right == workArea.Right ? spacing : half);
            bottom -= (int)Math.Round(bottom == workArea.Bottom ? spacing : half);

            list.Add(PixelRect.FromEdges(left, top, Math.Max(left + 1, right), Math.Max(top + 1, bottom)));
        }
        return list;
    }

    /// <summary>
    /// The zone under the point, or -1. Points in the spacing gutters snap to the nearest zone, so there
    /// are no dead strips between zones while dragging.
    /// </summary>
    public static int HitTest(IReadOnlyList<PixelRect> zones, int x, int y, PixelRect workArea)
    {
        if (x < workArea.X || x >= workArea.Right || y < workArea.Y || y >= workArea.Bottom) return -1;

        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < zones.Count; i++)
        {
            var d = DistanceSquared(zones[i], x, y);
            if (d == 0) return i;
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>
    /// The zones covered when spanning from zone <paramref name="from"/> to zone <paramref name="to"/>: every
    /// zone that lies inside their combined bounding box. Always includes both ends.
    /// </summary>
    public static IReadOnlyList<int> Span(IReadOnlyList<PixelRect> zones, int from, int to)
    {
        if (from < 0 || to < 0) return to >= 0 ? [to] : from >= 0 ? [from] : [];
        var box = Union(zones[from], zones[to]);

        var picked = new List<int>();
        for (var i = 0; i < zones.Count; i++)
        {
            // "Inside" with a little slack so spacing rounding doesn't drop a zone.
            if (i == from || i == to || Contains(box, zones[i], slack: 2))
                picked.Add(i);
        }
        return picked;
    }

    /// <summary>The bounding rectangle of the given zones.</summary>
    public static PixelRect Bounds(IReadOnlyList<PixelRect> zones, IEnumerable<int> indices)
    {
        PixelRect? acc = null;
        foreach (var i in indices)
            acc = acc is { } a ? Union(a, zones[i]) : zones[i];
        return acc ?? default;
    }

    /// <summary>The zone indices a rectangle exactly fills (within tolerance), or empty — used to tell
    /// whether a window still sits in its zones.</summary>
    public static IReadOnlyList<int> Occupied(IReadOnlyList<PixelRect> zones, PixelRect rect, int tolerance)
    {
        for (var from = 0; from < zones.Count; from++)
        {
            for (var to = 0; to < zones.Count; to++)
            {
                var span = Span(zones, from, to);
                if (Bounds(zones, span).RoughlyEquals(rect, tolerance))
                    return span;
            }
        }
        return [];
    }

    public static PixelRect Union(PixelRect a, PixelRect b)
        => PixelRect.FromEdges(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.Right, b.Right), Math.Max(a.Bottom, b.Bottom));

    private static bool Contains(PixelRect outer, PixelRect inner, int slack)
        => inner.X >= outer.X - slack && inner.Y >= outer.Y - slack
        && inner.Right <= outer.Right + slack && inner.Bottom <= outer.Bottom + slack;

    private static double DistanceSquared(PixelRect r, int x, int y)
    {
        var dx = x < r.X ? r.X - x : x >= r.Right ? x - r.Right + 1 : 0;
        var dy = y < r.Y ? r.Y - y : y >= r.Bottom ? y - r.Bottom + 1 : 0;
        return (double)dx * dx + (double)dy * dy;
    }
}
