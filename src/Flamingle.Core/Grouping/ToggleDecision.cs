using Flamingle.Core.Geometry;
using Flamingle.Core.Windows;

namespace Flamingle.Core.Grouping;

public enum FlockAction
{
    /// <summary>Minimise every member.</summary>
    Minimise,

    /// <summary>Restore any minimised members and bring the whole flock to the front.</summary>
    Restore,
}

/// <summary>
/// What one press of a flock's toggle should do. The rule is "bring it forward, then put it away": if the
/// flock is already fully up and in front — the focused window is a member, or no other window covers any of
/// it (say it's on another monitor) — minimise it; otherwise restore / raise it. That way a press from inside
/// some other app brings a buried flock to you first, rather than minimising windows that were merely hidden
/// behind something, but a flock already in plain view goes away on the first press.
/// </summary>
public static class ToggleDecision
{
    // Overlaps thinner than this (px) don't count as covering — windows that just touch, or a zone gap's worth.
    private const int MinOverlap = 16;

    /// <param name="memberMinimised">One flag per live member — true if that member is minimised.</param>
    /// <param name="focusInFlock">True if the focused window is a member of this flock.</param>
    /// <param name="covered">True if some other window sits over part of the flock (see <see cref="IsCovered"/>).</param>
    public static FlockAction Decide(IReadOnlyCollection<bool> memberMinimised, bool focusInFlock, bool covered = true)
    {
        // Any member down (all, or just some) → bring the whole flock back.
        if (memberMinimised.Count == 0 || memberMinimised.Any(m => m)) return FlockAction.Restore;
        return focusInFlock || !covered ? FlockAction.Minimise : FlockAction.Restore;
    }

    /// <summary>
    /// True if any (not minimised) member has a non-member window above it in Z order overlapping it by more than a
    /// sliver. <paramref name="windowsTopFirst"/> is what could cover — app windows in Z order, topmost first (always-
    /// on-top ones left out by the caller: raising the flock wouldn't get it above them anyway).
    /// </summary>
    public static bool IsCovered(IReadOnlyList<WindowSnapshot> windowsTopFirst, IReadOnlySet<long> members)
    {
        var above = new List<PixelRect>();
        foreach (var w in windowsTopFirst)
        {
            if (w.IsMinimised) continue;
            if (!members.Contains(w.Handle))
            {
                above.Add(w.Bounds);
                continue;
            }
            if (above.Any(a => Overlaps(a, w.Bounds))) return true;
        }
        return false;
    }

    private static bool Overlaps(PixelRect a, PixelRect b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > MinOverlap && h > MinOverlap;
    }
}
