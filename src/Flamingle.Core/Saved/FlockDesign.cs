using Flamingle.Core.Geometry;
using Flamingle.Core.Zones;

namespace Flamingle.Core.Saved;

/// <summary>A region of a monitor in design mode: one or more zones of its layout and the saved windows that go
/// there (by index). No windows = an empty zone, waiting to be given something to start.</summary>
public sealed record DesignRegion(IReadOnlyList<int> Zones, IReadOnlyList<int> Windows)
{
    public bool IsEmpty => Windows.Count == 0;
}

/// <summary>What design mode draws on one monitor: its layout's regions, and the windows that go to that monitor
/// but not into a zone of that layout (<see cref="Floating"/> — they keep a saved size and place).</summary>
public sealed record DesignScreen(MonitorInfo Monitor, string LayoutId, IReadOnlyList<DesignRegion> Regions, IReadOnlyList<int> Floating);

/// <summary>
/// The pure side of design mode — editing a saved flock by clicking where its windows go on the real screens. Works
/// out each monitor's regions from the saved windows' zones and edits where windows go (zones, layout) and what
/// starts there, without touching anything else a window carries (its recipe, desktop, Z-order).
/// </summary>
public static class FlockDesign
{
    /// <summary>The monitor a saved window goes to today (same as loading it: <see cref="PlacementMapper.Monitor"/>).</summary>
    public static MonitorInfo MonitorOf(SavedWindow w, IReadOnlyList<MonitorInfo> monitors)
        => PlacementMapper.Monitor(w.Placement.Monitor, w.Placement.WorkArea, monitors);

    /// <summary>The layout most of the flock's zoned windows on <paramref name="device"/> use (ties go to the topmost
    /// window's), or null if none of them are zoned there.</summary>
    public static string? LayoutOn(SavedFlock flock, string device, IReadOnlyList<MonitorInfo> monitors) => flock.Windows
        .Where(w => w.Zone is not null && MonitorOf(w, monitors).DeviceName == device)
        .OrderBy(w => w.ZIndex)
        .Select(w => w.Zone!.LayoutId)
        .GroupBy(id => id)
        .OrderByDescending(g => g.Count())
        .Select(g => g.Key)
        .FirstOrDefault();

    /// <summary>
    /// The regions of <paramref name="monitor"/> under <paramref name="layoutId"/> (which has
    /// <paramref name="zoneCount"/> zones): one per distinct set of zones the flock's windows there fill (windows
    /// sharing a set share the region), then one per zone nobody fills. Ordered by first zone.
    /// </summary>
    public static DesignScreen Screen(SavedFlock flock, MonitorInfo monitor, string layoutId, int zoneCount, IReadOnlyList<MonitorInfo> monitors)
    {
        var regions = new List<DesignRegion>();
        var floating = new List<int>();
        var covered = new HashSet<int>();
        for (var i = 0; i < flock.Windows.Count; i++)
        {
            var w = flock.Windows[i];
            if (MonitorOf(w, monitors).DeviceName != monitor.DeviceName) continue;
            List<int> zones = w.Zone is { } z && z.LayoutId == layoutId
                ? z.Zones.Where(n => n >= 0 && n < zoneCount).Distinct().Order().ToList()
                : [];
            if (zones.Count == 0)
            {
                floating.Add(i);
                continue;
            }
            var same = regions.FindIndex(r => r.Zones.SequenceEqual(zones));
            if (same >= 0) regions[same] = regions[same] with { Windows = [.. regions[same].Windows, i] };
            else regions.Add(new DesignRegion(zones, [i]));
            covered.UnionWith(zones);
        }
        for (var n = 0; n < zoneCount; n++)
        {
            if (!covered.Contains(n)) regions.Add(new DesignRegion([n], []));
        }
        return new DesignScreen(monitor, layoutId, regions.OrderBy(r => r.Zones[0]).ThenBy(r => r.Zones.Count).ToList(), floating);
    }

    /// <summary>
    /// Switch <paramref name="device"/> from layout <paramref name="from"/> to <paramref name="to"/>: every window
    /// there in a zone of <paramref name="from"/> moves to the zone of <paramref name="to"/> under the centre of each
    /// zone it was in (else the nearest). Windows in other layouts, or on other monitors, are left alone.
    /// </summary>
    public static SavedFlock SetLayout(SavedFlock flock, string device, ZoneLayout from, ZoneLayout to, IReadOnlyList<MonitorInfo> monitors)
    {
        if (from.Id == to.Id || to.Zones.Count == 0) return flock;
        return flock with
        {
            Windows = flock.Windows.Select(w =>
            {
                if (w.Zone is not { } z || z.LayoutId != from.Id || MonitorOf(w, monitors).DeviceName != device) return w;
                var zones = z.Zones.Select(n => MapZone(from, to, n)).Distinct().Order().ToList();
                return w with { Zone = new SavedZone(to.Id, zones) };
            }).ToList(),
        };
    }

    /// <summary>The zone of <paramref name="to"/> under the centre of zone <paramref name="zone"/> of
    /// <paramref name="from"/> (or nearest to it).</summary>
    public static int MapZone(ZoneLayout from, ZoneLayout to, int zone)
    {
        if (zone < 0 || zone >= from.Zones.Count) return Math.Clamp(zone, 0, to.Zones.Count - 1);
        var f = from.Zones[zone];
        var (cx, cy) = (f.X + f.Width / 2, f.Y + f.Height / 2);
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < to.Zones.Count; i++)
        {
            var t = to.Zones[i];
            var dx = cx < t.X ? t.X - cx : cx > t.X + t.Width ? cx - (t.X + t.Width) : 0;
            var dy = cy < t.Y ? t.Y - cy : cy > t.Y + t.Height ? cy - (t.Y + t.Height) : 0;
            var d = dx * dx + dy * dy;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return best;
    }

    /// <summary>Put window <paramref name="index"/> into <paramref name="zones"/> of <paramref name="layoutId"/> on
    /// <paramref name="monitor"/>; <paramref name="bounds"/> (the zones' outline, physical px) becomes its saved size
    /// and place in case the layout isn't there when it loads.</summary>
    public static SavedFlock Place(SavedFlock flock, int index, MonitorInfo monitor, string layoutId, IReadOnlyList<int> zones, PixelRect bounds)
        => With(flock, index, w => Placed(w, monitor, layoutId, zones, bounds));

    /// <summary><paramref name="w"/>, going into <paramref name="zones"/> on <paramref name="monitor"/>. Restored
    /// (not maximised); still minimised if it was saved minimised.</summary>
    public static SavedWindow Placed(SavedWindow w, MonitorInfo monitor, string layoutId, IReadOnlyList<int> zones, PixelRect bounds) => w with
    {
        Zone = new SavedZone(layoutId, zones.Distinct().Order().ToList()),
        Placement = new SavedPlacement(monitor.DeviceName, monitor.WorkArea, monitor.Scale, bounds, null, Maximised: false, w.Placement.Minimised),
    };

    /// <summary>Add a window behind the others (last in Z-order).</summary>
    public static SavedFlock Add(SavedFlock flock, SavedWindow w)
    {
        var z = flock.Windows.Count == 0 ? 0 : flock.Windows.Max(x => x.ZIndex) + 1;
        return flock with { Windows = [.. flock.Windows, w with { ZIndex = z }] };
    }

    /// <summary>Start a different app in window <paramref name="index"/>'s place: the new app keeps where the old one
    /// went (zones, monitor, desktop, Z-order). The old recipe goes — it was for the old app.</summary>
    public static SavedFlock Replace(SavedFlock flock, int index, SavedWindow app)
        => With(flock, index, old => app with
        {
            Placement = old.Placement,
            Zone = old.Zone,
            ZIndex = old.ZIndex,
            Desktop = old.Desktop,
        });

    public static SavedFlock Remove(SavedFlock flock, int index)
        => flock with { Windows = flock.Windows.Where((_, i) => i != index).ToList() };

    /// <summary>A program picked rather than captured (from the installed apps, or browsed to): nothing about a
    /// window is known yet, so it's recognised by its exe alone. A start-in folder becomes part of its recipe.</summary>
    public static SavedWindow ForProgram(string exePath, string arguments = "", string? startIn = null)
    {
        var w = new SavedWindow(Path.GetFileNameWithoutExtension(exePath), exePath, arguments, null, false, "", "", Unplaced, 0);
        return string.IsNullOrWhiteSpace(startIn) ? w : w.WithRecipe(w.EffectiveRecipe() with { StartIn = startIn });
    }

    /// <summary>A Store app picked from the installed apps, recognised by its AppUserModelID.</summary>
    public static SavedWindow ForStoreApp(string appUserModelId, string name)
        => new(name, null, "", appUserModelId, true, "", "", Unplaced, 0);

    // Picked windows are placed straight after; this only fills the field until then.
    private static readonly SavedPlacement Unplaced = new("", default, 1.0, default, null, false, false);

    private static SavedFlock With(SavedFlock flock, int index, Func<SavedWindow, SavedWindow> change)
        => flock with { Windows = flock.Windows.Select((w, i) => i == index ? change(w) : w).ToList() };
}
