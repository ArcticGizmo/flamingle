namespace Flamingle.Core.Zones;

/// <summary>
/// Files zone layouts under monitor + virtual desktop, or folds them back to one per monitor, as the per-desktop
/// toggle flips — so desktops whose layouts differ never re-flow each other's windows.
/// </summary>
public static class ZoneDesktops
{
    /// <summary>
    /// Tie every layout not yet on a desktop to one: the desktop most of its windows are on, else
    /// <paramref name="current"/>. Its windows on other desktops move to that desktop's layout on the monitor — a
    /// <see cref="ZoneContainer.Twin"/> if there isn't one. Windows whose desktop can't be read stay with the layout.
    /// </summary>
    /// <returns>The layouts that took windows in (to re-flow).</returns>
    public static IReadOnlyList<ZoneContainer> Split(List<ZoneContainer> containers, Func<long, Guid?> desktopOf, Guid? current)
    {
        var gained = new List<ZoneContainer>();
        foreach (var c in containers.Where(c => c.Desktop is null).ToList())
        {
            var slots = c.Slots.Values.Select(s => (Slot: s, Desktop: desktopOf(s.Handle))).ToList();
            var home = slots.Where(s => s.Desktop is not null)
                           .GroupBy(s => s.Desktop)
                           .OrderByDescending(g => g.Count())
                           .FirstOrDefault()?.Key
                       ?? current;
            if (home is null) continue;   // no desktop to go on

            // This desktop may already have its own layout on the monitor: that one wins.
            var existing = On(containers, c.DeviceName, home);
            foreach (var (slot, desktop) in slots)
            {
                var key = desktop ?? home;
                var target = key == home ? existing ?? c : On(containers, c.DeviceName, key) ?? Add(containers, c.Twin(key));
                if (target == c) continue;
                c.Slots.Remove(slot.Handle);
                target.Slots[slot.Handle] = slot;
                if (!gained.Contains(target)) gained.Add(target);
            }

            if (existing is null) c.Desktop = home;
            else containers.Remove(c);
        }
        return gained;
    }

    /// <summary>
    /// Fold each monitor's layouts back into one that serves every desktop: the one on <paramref name="current"/> is
    /// kept (else the first) and takes in the others' windows.
    /// </summary>
    /// <returns>The layouts kept (to re-flow).</returns>
    public static IReadOnlyList<ZoneContainer> Merge(List<ZoneContainer> containers, Guid? current)
    {
        var kept = new List<ZoneContainer>();
        foreach (var monitor in containers.GroupBy(c => c.DeviceName).ToList())
        {
            if (monitor.Count() == 1 && monitor.First().Desktop is null) continue;
            var keep = monitor.FirstOrDefault(c => c.Desktop == current) ?? monitor.First();
            keep.Desktop = null;
            foreach (var other in monitor.Where(c => c != keep))
            {
                foreach (var s in other.Slots.Values) keep.Slots[s.Handle] = s;
                containers.Remove(other);
            }
            kept.Add(keep);
        }
        return kept;
    }

    /// <summary>
    /// Drop empty layouts left on desktops that have been closed (Windows moves a closed desktop's windows to another,
    /// and zoned ones follow into that desktop's layout). Layouts with windows stay, as does everything if
    /// <paramref name="desktops"/> is empty — a desktop list that couldn't be read is no reason to throw layouts away.
    /// </summary>
    /// <returns>True if any went.</returns>
    public static bool PruneClosed(List<ZoneContainer> containers, IReadOnlyCollection<Guid> desktops)
        => desktops.Count > 0
           && containers.RemoveAll(c => c is { Desktop: { } d, Slots.Count: 0 } && !desktops.Contains(d)) > 0;

    private static ZoneContainer? On(List<ZoneContainer> containers, string deviceName, Guid? desktop)
        => containers.FirstOrDefault(c => c.DeviceName == deviceName && c.Desktop == desktop);

    private static ZoneContainer Add(List<ZoneContainer> containers, ZoneContainer c)
    {
        containers.Add(c);
        return c;
    }
}
