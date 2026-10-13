namespace Flamingle.Core.Grouping;

/// <summary>
/// Which virtual desktop a flock "lives" on — where its taskbar button belongs. The desktop holding most of
/// its members wins; a tie goes to the desktop of the member the user last focused, else to the earliest
/// member's desktop.
/// </summary>
public static class FlockDesktop
{
    /// <param name="memberDesktops">One entry per member, in flock order — <see cref="Guid.Empty"/> where the
    /// desktop couldn't be read.</param>
    /// <param name="focusedDesktop">Desktop of the last-focused window if it's a member, else null.</param>
    /// <returns>The flock's desktop, or null when no member's desktop is known.</returns>
    public static Guid? Choose(IReadOnlyList<Guid> memberDesktops, Guid? focusedDesktop)
    {
        var known = memberDesktops.Where(d => d != Guid.Empty).ToList();
        if (known.Count == 0) return null;

        var counts = known.GroupBy(d => d).ToDictionary(g => g.Key, g => g.Count());
        var best = counts.Values.Max();
        var tied = counts.Where(kv => kv.Value == best).Select(kv => kv.Key).ToHashSet();

        if (focusedDesktop is { } f && tied.Contains(f)) return f;
        return known.First(tied.Contains);
    }
}
