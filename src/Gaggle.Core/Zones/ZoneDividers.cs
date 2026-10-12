using Gaggle.Core.Geometry;

namespace Gaggle.Core.Zones;

/// <summary>
/// A draggable line between zones. <see cref="Position"/> is the fraction (x for a vertical divider, y for a
/// horizontal one) where the zones in <see cref="Before"/> end and the zones in <see cref="After"/> begin;
/// <see cref="Start"/>..<see cref="End"/> is how far it runs along the other axis.
/// </summary>
public sealed record ZoneDivider(bool Vertical, double Position, double Start, double End, IReadOnlyList<int> Before, IReadOnlyList<int> After);

/// <summary>
/// Finds the dividers in a layout and moves them. A divider runs as far as the zones that share it: in a
/// 2 × 2 grid the middle vertical line spans both rows (so it resizes all four zones), while in "main +
/// side stack" the horizontal line only spans the stack.
/// </summary>
public static class ZoneDividers
{
    private const double Eps = 1e-6;

    public static IReadOnlyList<ZoneDivider> Find(IReadOnlyList<ZoneFraction> zones)
    {
        var list = new List<ZoneDivider>();
        list.AddRange(FindAlong(zones, vertical: true));
        list.AddRange(FindAlong(zones, vertical: false));
        return list;
    }

    public enum Edge { Left, Top, Right, Bottom }

    /// <summary>
    /// The divider along one edge of a window that fills <paramref name="slotZones"/> — the line a user is
    /// dragging when they resize that window by that edge. Null for an outer edge (nothing on the other side).
    /// </summary>
    public static ZoneDivider? ForEdge(IReadOnlyList<ZoneFraction> zones, IReadOnlyList<int> slotZones, Edge edge)
    {
        var mine = slotZones.Where(i => i >= 0 && i < zones.Count).ToList();
        if (mine.Count == 0) return null;

        var vertical = edge is Edge.Left or Edge.Right;
        var trailing = edge is Edge.Right or Edge.Bottom;
        var line = trailing ? mine.Max(i => Trail(zones[i], vertical)) : mine.Min(i => Lead(zones[i], vertical));

        return Find(zones).FirstOrDefault(d =>
            d.Vertical == vertical
            && Math.Abs(d.Position - line) < Eps
            && (trailing ? d.Before : d.After).Any(mine.Contains));
    }

    /// <summary>
    /// Move a divider to <paramref name="position"/> (clamped so no zone it touches gets narrower than
    /// <paramref name="minSize"/>, as a fraction). Returns the new zones; the input is not modified.
    /// </summary>
    public static IReadOnlyList<ZoneFraction> Move(IReadOnlyList<ZoneFraction> zones, ZoneDivider divider, double position, double minSize)
    {
        var lo = divider.Before.Max(i => Lead(zones[i], divider.Vertical)) + minSize;
        var hi = divider.After.Min(i => Trail(zones[i], divider.Vertical)) - minSize;
        var p = lo > hi ? divider.Position : Math.Clamp(position, lo, hi);

        var result = zones.ToList();
        foreach (var i in divider.Before)
        {
            var z = result[i];
            result[i] = divider.Vertical ? z with { Width = p - z.X } : z with { Height = p - z.Y };
        }
        foreach (var i in divider.After)
        {
            var z = result[i];
            result[i] = divider.Vertical
                ? z with { X = p, Width = z.X + z.Width - p }
                : z with { Y = p, Height = z.Y + z.Height - p };
        }
        return result;
    }

    /// <summary>
    /// Where to draw a divider's handle: the gap between the resolved zones on either side (at least
    /// <paramref name="minThickness"/> px thick, centred on the line when the gap is narrower).
    /// </summary>
    public static PixelRect HandleRect(ZoneDivider d, IReadOnlyList<PixelRect> resolved, int minThickness)
    {
        var before = d.Before.Select(i => resolved[i]).ToList();
        var after = d.After.Select(i => resolved[i]).ToList();
        var all = before.Concat(after).ToList();

        if (d.Vertical)
        {
            var left = before.Max(r => r.Right);
            var right = after.Min(r => r.X);
            (left, right) = Thicken(left, right, minThickness);
            return PixelRect.FromEdges(left, all.Min(r => r.Y), right, all.Max(r => r.Bottom));
        }
        else
        {
            var top = before.Max(r => r.Bottom);
            var bottom = after.Min(r => r.Y);
            (top, bottom) = Thicken(top, bottom, minThickness);
            return PixelRect.FromEdges(all.Min(r => r.X), top, all.Max(r => r.Right), bottom);
        }
    }

    private static (int, int) Thicken(int a, int b, int min)
    {
        if (b - a >= min) return (a, b);
        var mid = (a + b) / 2;
        return (mid - min / 2, mid - min / 2 + min);
    }

    private static IEnumerable<ZoneDivider> FindAlong(IReadOnlyList<ZoneFraction> zones, bool vertical)
    {
        // Every inner edge where some zone ends and another begins.
        var positions = zones
            .Select(z => Trail(z, vertical))
            .Where(p => p < 1 - Eps && zones.Any(o => Math.Abs(Lead(o, vertical) - p) < Eps))
            .Distinct(new NearlyEqual())
            .OrderBy(p => p);

        foreach (var p in positions)
        {
            var touching = Enumerable.Range(0, zones.Count)
                .Where(i => Math.Abs(Trail(zones[i], vertical) - p) < Eps || Math.Abs(Lead(zones[i], vertical) - p) < Eps)
                .OrderBy(i => Across(zones[i], vertical).Start)
                .ToList();

            // Split into runs whose spans along the line touch or overlap — each run is one divider.
            var run = new List<int>();
            var runEnd = double.MinValue;
            foreach (var i in touching)
            {
                var (s, e) = Across(zones[i], vertical);
                if (run.Count > 0 && s > runEnd + Eps)
                {
                    if (Make(zones, vertical, p, run) is { } d) yield return d;
                    run = [];
                    runEnd = double.MinValue;
                }
                run.Add(i);
                runEnd = Math.Max(runEnd, e);
            }
            if (Make(zones, vertical, p, run) is { } last) yield return last;
        }
    }

    private static ZoneDivider? Make(IReadOnlyList<ZoneFraction> zones, bool vertical, double p, List<int> run)
    {
        var before = run.Where(i => Math.Abs(Trail(zones[i], vertical) - p) < Eps).ToList();
        var after = run.Where(i => Math.Abs(Lead(zones[i], vertical) - p) < Eps).ToList();
        if (before.Count == 0 || after.Count == 0) return null;
        return new ZoneDivider(vertical, p,
            run.Min(i => Across(zones[i], vertical).Start),
            run.Max(i => Across(zones[i], vertical).End),
            before, after);
    }

    // Lead/trail edge along the divider's axis; the span across it.
    private static double Lead(ZoneFraction z, bool vertical) => vertical ? z.X : z.Y;
    private static double Trail(ZoneFraction z, bool vertical) => vertical ? z.X + z.Width : z.Y + z.Height;
    private static (double Start, double End) Across(ZoneFraction z, bool vertical)
        => vertical ? (z.Y, z.Y + z.Height) : (z.X, z.X + z.Width);

    private sealed class NearlyEqual : IEqualityComparer<double>
    {
        public bool Equals(double a, double b) => Math.Abs(a - b) < Eps;
        public int GetHashCode(double v) => Math.Round(v, 5).GetHashCode();
    }
}
