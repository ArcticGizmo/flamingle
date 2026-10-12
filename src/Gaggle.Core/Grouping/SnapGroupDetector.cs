using Gaggle.Core.Geometry;
using Gaggle.Core.Windows;

namespace Gaggle.Core.Grouping;

/// <summary>
/// Guesses which windows are snapped together with a seed window. Windows exposes no public API for Snap
/// Groups, so this is purely geometric: starting from the seed, it walks to every window on the same
/// monitor whose visible frame <em>abuts</em> one already in the group (shares an edge, within a
/// tolerance) without substantially <em>overlapping</em> any of them. Snapped tiles touch but don't
/// overlap; free-floating windows usually do one or neither.
///
/// <para>It's a heuristic — the result pre-checks the picker for the user to confirm, it never creates a
/// gaggle on its own.</para>
/// </summary>
public static class SnapGroupDetector
{
    /// <summary>How far apart two edges can be (physical px) and still count as touching.</summary>
    public const int DefaultEdgeTolerance = 16;

    /// <summary>How much two touching edges must run alongside each other to count as neighbours.</summary>
    public const int DefaultMinSharedEdge = 48;

    /// <summary>Overlap above this fraction of the smaller window means "stacked", not "tiled".</summary>
    public const double MaxOverlapFraction = 0.10;

    /// <summary>
    /// The handles of the windows snapped with <paramref name="seed"/> (seed included), in the order they
    /// appear in <paramref name="windows"/>. Just the seed if it's minimised/maximised or has no neighbours;
    /// empty if the seed isn't in the list.
    /// </summary>
    public static IReadOnlyList<long> Detect(
        long seed,
        IReadOnlyList<WindowSnapshot> windows,
        int edgeTolerance = DefaultEdgeTolerance,
        int minSharedEdge = DefaultMinSharedEdge)
    {
        var seedWindow = windows.FirstOrDefault(w => w.Handle == seed);
        if (seedWindow is null) return [];
        if (seedWindow.IsMinimised || seedWindow.IsMaximised) return [seed];

        var pool = windows
            .Where(w => !w.IsMinimised && !w.IsMaximised && w.Monitor == seedWindow.Monitor)
            .ToList();

        var group = new List<WindowSnapshot> { seedWindow };
        var queue = new Queue<WindowSnapshot>([seedWindow]);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var other in pool)
            {
                if (group.Contains(other)) continue;
                if (!Touches(current.Bounds, other.Bounds, edgeTolerance, minSharedEdge)) continue;
                if (group.Any(g => OverlapsSignificantly(g.Bounds, other.Bounds))) continue;

                group.Add(other);
                queue.Enqueue(other);
            }
        }

        return windows.Where(group.Contains).Select(w => w.Handle).ToList();
    }

    /// <summary>True when the two rects share a left/right or top/bottom edge (within tolerance) along
    /// at least <paramref name="minSharedEdge"/> pixels.</summary>
    internal static bool Touches(PixelRect a, PixelRect b, int tolerance, int minSharedEdge)
    {
        var sideBySide = Math.Abs(a.Right - b.X) <= tolerance || Math.Abs(b.Right - a.X) <= tolerance;
        if (sideBySide && SpanOverlap(a.Y, a.Bottom, b.Y, b.Bottom) >= minSharedEdge)
            return true;

        var stacked = Math.Abs(a.Bottom - b.Y) <= tolerance || Math.Abs(b.Bottom - a.Y) <= tolerance;
        return stacked && SpanOverlap(a.X, a.Right, b.X, b.Right) >= minSharedEdge;
    }

    internal static bool OverlapsSignificantly(PixelRect a, PixelRect b)
    {
        var smaller = Math.Min(a.Area, b.Area);
        return smaller > 0 && a.IntersectionArea(b) > smaller * MaxOverlapFraction;
    }

    private static int SpanOverlap(int a0, int a1, int b0, int b1) => Math.Min(a1, b1) - Math.Max(a0, b0);
}
