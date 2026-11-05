namespace Flamingle.Core.Zones;

/// <summary>
/// Layouts filed under the wrong key for a monitor. Mid display change Windows can report a monitor before its PnP
/// instance id can be read, so it's keyed by its GDI name for a moment — and a window dropped then starts a second
/// layout on the same screen. Once the stable key is known, the stray folds into the real one.
/// </summary>
public static class ZoneMonitorKeys
{
    /// <summary>
    /// Re-file every layout on <paramref name="from"/> under <paramref name="to"/>. Where <paramref name="to"/>
    /// already has a layout on that desktop, that one wins (its preset, frame and splits) and takes in the stray's
    /// windows; otherwise the stray is simply re-keyed.
    /// </summary>
    /// <returns>The layouts that were re-keyed or took windows in (to re-flow).</returns>
    public static IReadOnlyList<ZoneContainer> Fold(List<ZoneContainer> containers, string from, string to)
    {
        var touched = new List<ZoneContainer>();
        if (from == to) return touched;
        foreach (var stray in containers.Where(c => c.DeviceName == from).ToList())
        {
            var home = containers.FirstOrDefault(c => c.DeviceName == to && c.Desktop == stray.Desktop);
            if (home is null)
            {
                stray.DeviceName = to;
                touched.Add(stray);
                continue;
            }
            foreach (var s in stray.Slots.Values) home.Slots[s.Handle] = s;
            containers.Remove(stray);
            if (!touched.Contains(home)) touched.Add(home);
        }
        return touched;
    }
}
