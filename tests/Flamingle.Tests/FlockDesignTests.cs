using Flamingle.Core.Geometry;
using Flamingle.Core.Saved;
using Flamingle.Core.Zones;

namespace Flamingle.Tests;

public class FlockDesignTests
{
    private static readonly MonitorInfo Left = new(1, @"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), 1.0, true);
    private static readonly MonitorInfo Right = new(2, @"\\.\DISPLAY2", new PixelRect(1920, 0, 2560, 1440), new PixelRect(1920, 0, 2560, 1400), 1.25, false);
    private static readonly IReadOnlyList<MonitorInfo> Monitors = [Left, Right];

    private static readonly ZoneLayout Three = ZoneLayout.ById("columns-3");
    private static readonly ZoneLayout Two = ZoneLayout.ById("columns-2");

    private static SavedWindow Window(string exe, MonitorInfo? on = null, SavedZone? zone = null, int z = 0, LaunchRecipe? recipe = null)
    {
        var m = on ?? Left;
        return new SavedWindow(Path.GetFileNameWithoutExtension(exe), exe, "", null, false, "Cls", "title",
            new SavedPlacement(m.DeviceName, m.WorkArea, m.Scale, new PixelRect(m.WorkArea.X + 10, 10, 800, 600), null, false, false),
            z, Zone: zone, Recipe: recipe);
    }

    private static SavedFlock Flock(params SavedWindow[] windows) => new(Guid.NewGuid(), "f", DateTimeOffset.UnixEpoch, [.. windows]);

    [Fact]
    public void A_screen_has_a_region_per_filled_zone_set_then_one_per_empty_zone()
    {
        var flock = Flock(
            Window(@"C:\a.exe", zone: new SavedZone("columns-3", [0, 1])),
            Window(@"C:\b.exe", zone: new SavedZone("columns-3", [1, 0]), z: 1),   // same zones, other order: same region
            Window(@"C:\c.exe", z: 2),                                             // not zoned: floating
            Window(@"C:\d.exe", Right, new SavedZone("columns-3", [2]), z: 3));    // another monitor

        var screen = FlockDesign.Screen(flock, Left, "columns-3", 3, Monitors);

        Assert.Equal(2, screen.Regions.Count);
        Assert.Equal([0, 1], screen.Regions[0].Zones);
        Assert.Equal([0, 1], screen.Regions[0].Windows);
        Assert.Equal([2], screen.Regions[1].Zones);
        Assert.True(screen.Regions[1].IsEmpty);
        Assert.Equal([2], screen.Floating);
    }

    [Fact]
    public void Windows_in_another_layout_float_and_out_of_range_zones_are_dropped()
    {
        var flock = Flock(
            Window(@"C:\a.exe", zone: new SavedZone("grid-2x2", [0])),
            Window(@"C:\b.exe", zone: new SavedZone("columns-2", [1, 7])));

        var screen = FlockDesign.Screen(flock, Left, "columns-2", 2, Monitors);

        Assert.Equal([0], screen.Floating);
        Assert.Equal([1], screen.Regions.Single(r => !r.IsEmpty).Zones);
    }

    [Fact]
    public void A_window_saved_on_a_monitor_thats_gone_shows_on_the_one_it_would_load_onto()
    {
        var gone = new MonitorInfo(9, @"\\.\DISPLAY9", default, new PixelRect(0, 0, 2560, 1400), 1.0, false);
        var flock = Flock(Window(@"C:\a.exe", gone, new SavedZone("columns-2", [0])));

        Assert.Equal("columns-2", FlockDesign.LayoutOn(flock, Right.DeviceName, Monitors));   // same size as DISPLAY9
        Assert.Null(FlockDesign.LayoutOn(flock, Left.DeviceName, Monitors));
    }

    [Fact]
    public void Switching_layout_moves_each_zone_to_the_one_under_its_centre()
    {
        var flock = Flock(
            Window(@"C:\a.exe", zone: new SavedZone("columns-3", [0])),
            Window(@"C:\b.exe", zone: new SavedZone("columns-3", [2])),
            Window(@"C:\c.exe", zone: new SavedZone("columns-3", [1, 2])),
            Window(@"C:\d.exe", Right, new SavedZone("columns-3", [0])));

        var after = FlockDesign.SetLayout(flock, Left.DeviceName, Three, Two, Monitors);

        Assert.Equal(new SavedZone("columns-2", [0]).Zones, after.Windows[0].Zone!.Zones);
        Assert.Equal("columns-2", after.Windows[0].Zone!.LayoutId);
        Assert.Equal([1], after.Windows[1].Zone!.Zones);
        Assert.Equal([0, 1], after.Windows[2].Zone!.Zones);   // the middle third's centre sits on the split: nearest is left
        Assert.Equal("columns-3", after.Windows[3].Zone!.LayoutId);   // other monitor untouched
    }

    [Fact]
    public void Placing_a_window_sets_its_zones_and_saved_place_but_keeps_its_recipe()
    {
        var recipe = new LaunchRecipe(LaunchKind.Program, @"C:\a.exe", "--x");
        var flock = Flock(Window(@"C:\a.exe", recipe: recipe));
        var bounds = new PixelRect(1928, 8, 1264, 1384);

        var after = FlockDesign.Place(flock, 0, Right, "columns-2", [1, 0, 1], bounds);
        var w = after.Windows[0];

        Assert.Equal("columns-2", w.Zone!.LayoutId);
        Assert.Equal([0, 1], w.Zone.Zones);
        Assert.Equal(Right.DeviceName, w.Placement.Monitor);
        Assert.Equal(Right.WorkArea, w.Placement.WorkArea);
        Assert.Equal(bounds, w.Placement.Restored);
        Assert.False(w.Placement.Maximised);
        Assert.Equal(recipe, w.EffectiveRecipe());
    }

    [Fact]
    public void Replacing_an_app_keeps_where_it_went_but_not_the_old_recipe()
    {
        var old = Window(@"C:\a.exe", Right, new SavedZone("columns-2", [1]), z: 3,
            recipe: new LaunchRecipe(LaunchKind.Program, @"C:\a.exe", "--old")) with { Desktop = Guid.NewGuid() };
        var flock = Flock(old);

        var after = FlockDesign.Replace(flock, 0, FlockDesign.ForProgram(@"C:\Tools\new.exe", "--new"));
        var w = after.Windows[0];

        Assert.Equal("new", w.ProcessName);
        Assert.Equal(old.Zone, w.Zone);
        Assert.Equal(old.Placement, w.Placement);
        Assert.Equal(3, w.ZIndex);
        Assert.Equal(old.Desktop, w.Desktop);
        Assert.Equal("--new", w.EffectiveRecipe().Arguments);
        Assert.False(w.IsChanged);
    }

    [Fact]
    public void Added_windows_go_behind_and_picked_apps_get_the_right_recipe()
    {
        var flock = FlockDesign.Add(Flock(Window(@"C:\a.exe", z: 4)), FlockDesign.ForProgram(@"C:\b.exe", "-n", startIn: @"D:\src"));
        Assert.Equal(5, flock.Windows[1].ZIndex);
        Assert.Equal(new LaunchRecipe(LaunchKind.Program, @"C:\b.exe", "-n", StartIn: @"D:\src"), flock.Windows[1].EffectiveRecipe());

        var store = FlockDesign.ForStoreApp("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "Terminal");
        Assert.Equal(LaunchKind.StoreApp, store.EffectiveRecipe().Kind);
        Assert.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", store.AppKey);

        Assert.Single(FlockDesign.Remove(flock, 0).Windows);
    }
}
