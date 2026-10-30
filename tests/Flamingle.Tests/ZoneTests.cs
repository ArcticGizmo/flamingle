using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Zones;

namespace Flamingle.Tests;

public class ZoneMathTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Two_columns_split_the_work_area_exactly()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), Work);
        Assert.Equal([new PixelRect(0, 0, 960, 1040), new PixelRect(960, 0, 960, 1040)], zones);
    }

    [Fact]
    public void Resolves_relative_to_a_non_zero_work_area()
    {
        var work = new PixelRect(-1920, 40, 1920, 1000);
        var zones = ZoneMath.Resolve(ZoneLayout.ById("rows-2"), work);
        Assert.Equal([new PixelRect(-1920, 40, 1920, 500), new PixelRect(-1920, 540, 1920, 500)], zones);
    }

    [Fact]
    public void Three_columns_tile_without_pixel_gaps()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), new PixelRect(0, 0, 1000, 500));
        Assert.Equal(zones[0].Right, zones[1].X);
        Assert.Equal(zones[1].Right, zones[2].X);
        Assert.Equal(1000, zones[2].Right);
    }

    [Fact]
    public void Hit_test_finds_the_zone_and_snaps_gutters_to_the_nearest()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), Work);
        Assert.Equal(0, ZoneMath.HitTest(zones, 100, 500, Work));
        Assert.Equal(1, ZoneMath.HitTest(zones, 1500, 500, Work));
        Assert.Equal(1, ZoneMath.HitTest(zones, 960, 500, Work));   // on the shared edge
        Assert.Equal(-1, ZoneMath.HitTest(zones, 2000, 500, Work));  // off the work area

        // A gutter between zones (e.g. a custom split's rounding) snaps to the nearer zone.
        PixelRect[] gapped = [PixelRect.FromEdges(0, 0, 956, 1040), PixelRect.FromEdges(964, 0, 1920, 1040)];
        Assert.Equal(1, ZoneMath.HitTest(gapped, 962, 500, Work));
    }

    [Fact]
    public void Span_covers_everything_between_the_two_zones()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("grid-2x3"), Work);
        // Top-left (0) to bottom-middle (4) → columns 0–1 on both rows.
        Assert.Equal([0, 1, 3, 4], ZoneMath.Span(zones, 0, 4));
        Assert.Equal([2], ZoneMath.Span(zones, 2, 2));
        Assert.Equal([5], ZoneMath.Span(zones, -1, 5));
    }

    [Fact]
    public void Bounds_of_a_span_runs_edge_to_edge()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), Work);
        Assert.Equal(new PixelRect(0, 0, 1280, 1040), ZoneMath.Bounds(zones, [0, 1]));
    }

    [Fact]
    public void Occupied_recognises_a_window_filling_zones()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), Work);
        Assert.Equal([1, 2], ZoneMath.Occupied(zones, new PixelRect(641, 1, 1278, 1039), tolerance: 2));
        Assert.Empty(ZoneMath.Occupied(zones, new PixelRect(300, 300, 400, 400), tolerance: 2));
    }

    [Fact]
    public void Every_template_stays_inside_the_unit_square()
    {
        foreach (var layout in ZoneLayout.Templates)
        foreach (var z in layout.Zones)
        {
            Assert.InRange(z.X, 0, 1);
            Assert.InRange(z.Y, 0, 1);
            Assert.InRange(z.X + z.Width, 0, 1.000001);
            Assert.InRange(z.Y + z.Height, 0, 1.000001);
        }
    }

    [Fact]
    public void Unknown_layout_ids_fall_back_to_the_default()
        => Assert.Equal(ZoneLayout.DefaultId, ZoneLayout.ById("nope").Id);
}

public class ZoneContainerTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Zones_follow_the_frame_not_the_monitor()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-2", new PixelRect(100, 50, 1000, 600));
        Assert.Equal([new PixelRect(100, 50, 500, 600), new PixelRect(600, 50, 500, 600)], c.Zones());

        c.Frame = ZoneContainer.Offset(c.Frame, 200, 100);
        Assert.Equal(new PixelRect(300, 150, 500, 600), c.Zones()[0]);
    }

    [Fact]
    public void Target_covers_every_zone_in_the_slot()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), Work);
        var slot = new ZoneSlot(1, 1, [0, 1]);
        Assert.Equal(new PixelRect(0, 0, 1280, 1040), ZoneContainer.TargetFor(slot, zones));
    }

    [Fact]
    public void Target_falls_back_to_the_last_zone_after_switching_to_fewer_zones()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), Work);
        var slot = new ZoneSlot(1, 1, [2]);   // was the third of three columns
        Assert.Equal(zones[1], ZoneContainer.TargetFor(slot, zones));
    }

    [Fact]
    public void Clamp_slides_a_frame_back_on_screen()
        => Assert.Equal(new PixelRect(920, 0, 1000, 600),
            ZoneContainer.ClampInto(new PixelRect(1500, -40, 1000, 600), Work));

    [Fact]
    public void Clamp_shrinks_a_frame_too_big_for_a_smaller_monitor()
    {
        var small = new PixelRect(1920, 0, 1280, 984);
        Assert.Equal(small, ZoneContainer.ClampInto(new PixelRect(1900, 0, 1920, 1040), small));
    }

    [Fact]
    public void Refit_grows_a_full_screen_frame_when_a_side_panel_closes()
    {
        var withPanel = new PixelRect(0, 0, 1520, 1040);   // a 400 px panel docked on the right
        Assert.Equal(Work, ZoneContainer.Refit(withPanel, withPanel, Work));
    }

    [Fact]
    public void Refit_shrinks_a_full_screen_frame_when_a_side_panel_docks()
    {
        var withPanel = new PixelRect(400, 0, 1520, 1040);   // docked on the left
        Assert.Equal(withPanel, ZoneContainer.Refit(Work, Work, withPanel));
    }

    [Fact]
    public void Refit_leaves_edges_the_user_moved_away_from_the_screen_edge()
    {
        var frame = new PixelRect(0, 100, 1200, 800);   // flush left only
        var wider = new PixelRect(0, 0, 2000, 1040);
        Assert.Equal(frame, ZoneContainer.Refit(frame, Work, wider));
    }

    [Fact]
    public void Refit_keeps_a_floating_frame_on_screen_when_the_work_area_shrinks()
    {
        var frame = new PixelRect(1000, 100, 800, 600);
        var withPanel = new PixelRect(0, 0, 1520, 1040);
        Assert.Equal(new PixelRect(720, 100, 800, 600), ZoneContainer.Refit(frame, Work, withPanel));
    }

    [Fact]
    public void MoveBetween_makes_a_full_screen_layout_fill_the_other_monitor()
    {
        var big = new PixelRect(1920, 0, 2560, 1400);
        Assert.Equal(big, ZoneContainer.MoveBetween(Work, Work, big));
    }

    [Fact]
    public void MoveBetween_keeps_a_smaller_frame_in_the_same_relative_place()
    {
        var big = new PixelRect(1920, 0, 3840, 2080);   // twice the size
        Assert.Equal(new PixelRect(2120, 100, 1920, 1040),
            ZoneContainer.MoveBetween(new PixelRect(100, 50, 960, 520), Work, big));
    }

    [Fact]
    public void Custom_splits_apply_and_reset_with_a_new_preset()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-2", new PixelRect(0, 0, 1000, 500));
        c.CustomZones = [new ZoneFraction(0, 0, 0.7, 1), new ZoneFraction(0.7, 0, 0.3, 1)];
        Assert.Equal(new PixelRect(0, 0, 700, 500), c.Zones()[0]);

        c.LayoutId = "columns-2";   // picking a preset (even the same one) evens it out
        Assert.Null(c.CustomZones);
        Assert.Equal(new PixelRect(0, 0, 500, 500), c.Zones()[0]);
    }

    [Fact]
    public void Custom_splits_for_a_different_zone_count_are_ignored()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-3", new PixelRect(0, 0, 900, 500))
        {
            CustomZones = [new ZoneFraction(0, 0, 0.7, 1), new ZoneFraction(0.7, 0, 0.3, 1)],
        };
        Assert.Equal(3, c.Zones().Count);
    }

    [Fact]
    public void Restore_at_cursor_keeps_the_grab_point_under_the_pointer()
    {
        var current = new PixelRect(0, 100, 1000, 800);   // snapped: wide
        var restore = new PixelRect(500, 300, 400, 300);  // before: small

        // Grabbed a quarter of the way along → a quarter of the way along the restored width.
        var r = ZoneSlot.RestoreAtCursor(restore, current, cursorX: 250);
        Assert.Equal(new PixelRect(150, 100, 400, 300), r);
    }

    [Fact]
    public void Restore_under_grab_follows_the_cursor()
    {
        var grabbed = new PixelRect(0, 0, 1000, 800);     // zone-sized when the drag began
        var restore = new PixelRect(500, 300, 400, 300);  // pre-snap size

        // Grabbed half-way along the title bar, 10 px down; the cursor has since moved to (600, 210).
        var r = ZoneSlot.RestoreUnderGrab(restore, grabbed, grabX: 500, grabY: 10, cursorX: 600, cursorY: 210);
        Assert.Equal(new PixelRect(400, 200, 400, 300), r);
    }

    [Fact]
    public void Restore_survives_a_round_trip()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-2", new PixelRect(0, 0, 800, 600));
        c.Slots[1] = new ZoneSlot(1, 10, [0], new PixelRect(10, 20, 300, 200));
        var back = ZoneContainerState.From(c).ToContainer(_ => true);
        Assert.Equal(new PixelRect(10, 20, 300, 200), back.Slots[1].Restore);
    }

    [Fact]
    public void State_round_trip_drops_dead_slots()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "grid-2x2", new PixelRect(0, 0, 800, 600));
        c.Slots[1] = new ZoneSlot(1, 10, [0]);
        c.Slots[2] = new ZoneSlot(2, 20, [1, 3]);

        var back = ZoneContainerState.From(c).ToContainer(s => s.Handle == 2);
        Assert.Equal("grid-2x2", back.LayoutId);
        Assert.Equal(c.Frame, back.Frame);
        Assert.Equal([2L], back.Slots.Keys);
    }

    [Fact]
    public void Desktop_survives_a_round_trip()
    {
        var desktop = Guid.NewGuid();
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-2", new PixelRect(0, 0, 800, 600)) { Desktop = desktop };
        Assert.Equal(desktop, ZoneContainerState.From(c).ToContainer(_ => true).Desktop);
    }

    [Fact]
    public void A_twin_copies_the_layout_but_not_the_windows()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-3", new PixelRect(10, 0, 800, 600))
        {
            CustomZones = [new(0, 0, 0.5, 1), new(0.5, 0, 0.25, 1), new(0.75, 0, 0.25, 1)],
        };
        c.Slots[1] = new ZoneSlot(1, 10, [0]);
        var desktop = Guid.NewGuid();

        var twin = c.Twin(desktop);

        Assert.Equal((c.DeviceName, c.LayoutId, c.Frame, desktop), (twin.DeviceName, twin.LayoutId, twin.Frame, twin.Desktop));
        Assert.Equal(c.CustomZones, twin.CustomZones);
        Assert.Empty(twin.Slots);
    }
}

public class ZoneDesktopTests
{
    private const string Display1 = @"\\.\DISPLAY1";
    private const string Display2 = @"\\.\DISPLAY2";
    private static readonly Guid DesktopA = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid DesktopB = new("bbbbbbbb-0000-0000-0000-000000000000");

    private static ZoneContainer Layout(string device, Guid? desktop, params long[] handles)
    {
        var c = new ZoneContainer(device, "columns-2", new PixelRect(0, 0, 800, 600)) { Desktop = desktop };
        foreach (var h in handles) c.Slots[h] = new ZoneSlot(h, 1, [0]);
        return c;
    }

    [Fact]
    public void Split_gives_each_desktop_its_own_copy_of_the_layout()
    {
        var shared = Layout(Display1, null, 1, 2, 3);
        shared.CustomZones = [new(0, 0, 0.3, 1), new(0.3, 0, 0.7, 1)];
        var containers = new List<ZoneContainer> { shared };
        var desktops = new Dictionary<long, Guid?> { [1] = DesktopA, [2] = DesktopB, [3] = DesktopA };

        var gained = ZoneDesktops.Split(containers, h => desktops[h], current: DesktopB);

        // Most of its windows were on A, so it stays there; B gets a copy with B's window.
        Assert.Equal(DesktopA, shared.Desktop);
        Assert.Equal([1L, 3L], shared.Slots.Keys.Order());
        var b = Assert.Single(containers, c => c.Desktop == DesktopB);
        Assert.Equal([2L], b.Slots.Keys);
        Assert.Equal(shared.CustomZones, b.CustomZones);
        Assert.Equal([b], gained);
    }

    [Fact]
    public void Split_puts_an_empty_layout_on_the_current_desktop()
    {
        var empty = Layout(Display1, null);
        var containers = new List<ZoneContainer> { empty };

        ZoneDesktops.Split(containers, _ => null, current: DesktopB);

        Assert.Equal(DesktopB, empty.Desktop);
        Assert.Single(containers);
    }

    [Fact]
    public void Split_keeps_windows_whose_desktop_cant_be_read_with_the_layout()
    {
        var shared = Layout(Display1, null, 1, 2);
        var containers = new List<ZoneContainer> { shared };

        ZoneDesktops.Split(containers, h => h == 1 ? DesktopA : null, current: DesktopB);

        Assert.Equal(DesktopA, shared.Desktop);
        Assert.Equal([1L, 2L], shared.Slots.Keys.Order());
        Assert.Single(containers);
    }

    [Fact]
    public void Split_leaves_a_layout_alone_when_no_desktop_can_be_read()
    {
        var shared = Layout(Display1, null, 1);
        var containers = new List<ZoneContainer> { shared };

        ZoneDesktops.Split(containers, _ => null, current: null);

        Assert.Null(shared.Desktop);
        Assert.Single(containers);
    }

    [Fact]
    public void Split_folds_into_a_desktops_existing_layout()
    {
        var own = Layout(Display1, DesktopA, 1);
        var stray = Layout(Display1, null, 2);
        var containers = new List<ZoneContainer> { own, stray };

        var gained = ZoneDesktops.Split(containers, _ => DesktopA, current: DesktopA);

        Assert.Equal([own], containers);
        Assert.Equal([1L, 2L], own.Slots.Keys.Order());
        Assert.Equal([own], gained);
    }

    [Fact]
    public void Split_leaves_layouts_already_on_a_desktop_alone()
    {
        var a = Layout(Display1, DesktopA, 1);
        var containers = new List<ZoneContainer> { a };

        var gained = ZoneDesktops.Split(containers, _ => DesktopB, current: DesktopB);

        Assert.Equal(DesktopA, a.Desktop);
        Assert.Equal([1L], a.Slots.Keys);
        Assert.Empty(gained);
    }

    [Fact]
    public void Merge_keeps_the_current_desktops_layout_per_monitor()
    {
        var a = Layout(Display1, DesktopA, 1);
        var b = Layout(Display1, DesktopB, 2);
        var other = Layout(Display2, DesktopA, 3);
        var containers = new List<ZoneContainer> { a, b, other };

        var kept = ZoneDesktops.Merge(containers, current: DesktopB);

        Assert.Equal([b, other], containers);
        Assert.Equal([1L, 2L], b.Slots.Keys.Order());
        Assert.All(containers, c => Assert.Null(c.Desktop));
        Assert.Equal([b, other], kept);
    }

    [Fact]
    public void Prune_drops_empty_layouts_on_closed_desktops()
    {
        var open = Layout(Display1, DesktopA);
        var closed = Layout(Display1, DesktopB);
        var shared = Layout(Display2, null);
        var containers = new List<ZoneContainer> { open, closed, shared };

        Assert.True(ZoneDesktops.PruneClosed(containers, [DesktopA]));
        Assert.Equal([open, shared], containers);
    }

    [Fact]
    public void Prune_keeps_a_closed_desktops_layout_while_it_has_windows()
    {
        var closed = Layout(Display1, DesktopB, 1);
        var containers = new List<ZoneContainer> { closed };

        Assert.False(ZoneDesktops.PruneClosed(containers, [DesktopA]));
        Assert.Equal([closed], containers);
    }

    [Fact]
    public void Prune_keeps_everything_when_the_desktops_are_unknown()
    {
        var containers = new List<ZoneContainer> { Layout(Display1, DesktopA) };

        Assert.False(ZoneDesktops.PruneClosed(containers, []));
        Assert.Single(containers);
    }

    [Fact]
    public void Merge_leaves_a_shared_layout_alone()
    {
        var shared = Layout(Display1, null, 1);
        var containers = new List<ZoneContainer> { shared };

        Assert.Empty(ZoneDesktops.Merge(containers, current: DesktopA));
        Assert.Equal([shared], containers);
    }
}

public class ZoneFlockRegistryTests
{
    private static FlockMember M(long handle) => new(handle, 1, $"app{handle}", $"Window {handle}");

    [Fact]
    public void Swapping_layouts_swaps_their_zone_flocks()
    {
        var reg = new FlockRegistry();
        var one = reg.Create("Zones · DISPLAY1", [M(1)], zoneMonitor: @"\\.\DISPLAY1");
        var two = reg.Create("Zones · DISPLAY2", [M(2)], zoneMonitor: @"\\.\DISPLAY2");

        reg.SwapZoneFlocks(@"\\.\DISPLAY1", @"\\.\DISPLAY2", g => $"Zones · {g.ZoneMonitor![4..]}");

        Assert.Equal((@"\\.\DISPLAY2", "Zones · DISPLAY2"), (one.ZoneMonitor, one.Name));
        Assert.Equal((@"\\.\DISPLAY1", "Zones · DISPLAY1"), (two.ZoneMonitor, two.Name));
    }

    [Fact]
    public void Add_moves_a_window_into_an_existing_flock()
    {
        var reg = new FlockRegistry();
        var hand = reg.Create("Hand", [M(1), M(2)]);
        var zones = reg.Create("Zones", [M(3)], zoneMonitor: @"\\.\DISPLAY1");

        reg.Add(zones, M(2));

        Assert.Equal([1L], hand.Members.Select(m => m.Handle));
        Assert.Equal([3L, 2L], zones.Members.Select(m => m.Handle));
        Assert.Same(zones, reg.ForZone(@"\\.\DISPLAY1", desktop: null));
        Assert.Null(reg.ForZone(@"\\.\DISPLAY2", desktop: null));
    }

    [Fact]
    public void Rehoming_moves_and_renames_the_zone_flock()
    {
        var reg = new FlockRegistry();
        var g = reg.Create("Zones · DISPLAY1", [M(1)], zoneMonitor: @"\\.\DISPLAY1");

        reg.RehomeZoneFlocks(@"\\.\DISPLAY1", @"\\.\DISPLAY2", _ => "Zones · DISPLAY2");

        Assert.Same(g, reg.ForZone(@"\\.\DISPLAY2", desktop: null));
        Assert.Null(reg.ForZone(@"\\.\DISPLAY1", desktop: null));
        Assert.Equal("Zones · DISPLAY2", g.Name);
    }

    private static readonly Guid DesktopA = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid DesktopB = new("bbbbbbbb-0000-0000-0000-000000000000");

    [Fact]
    public void Each_desktop_has_its_own_zone_flock_on_a_monitor()
    {
        var reg = new FlockRegistry();
        var a = reg.Create("A", [M(1)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopA);
        var b = reg.Create("B", [M(2)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopB);

        Assert.Same(a, reg.ForZone(@"\\.\DISPLAY1", DesktopA));
        Assert.Same(b, reg.ForZone(@"\\.\DISPLAY1", DesktopB));
        Assert.Null(reg.ForZone(@"\\.\DISPLAY1", Guid.NewGuid()));
    }

    [Fact]
    public void Rehoming_moves_every_desktops_zone_flock()
    {
        var reg = new FlockRegistry();
        var a = reg.Create("A", [M(1)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopA);
        var b = reg.Create("B", [M(2)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopB);

        reg.RehomeZoneFlocks(@"\\.\DISPLAY1", @"\\.\DISPLAY2", g => $"Zones · {g.ZoneDesktop == DesktopA}");

        Assert.Same(a, reg.ForZone(@"\\.\DISPLAY2", DesktopA));
        Assert.Same(b, reg.ForZone(@"\\.\DISPLAY2", DesktopB));
        Assert.Equal("Zones · True", a.Name);
        Assert.Equal("Zones · False", b.Name);
    }

    [Fact]
    public void Rehoming_one_desktops_layout_leaves_other_desktops_zone_flocks()
    {
        var reg = new FlockRegistry();
        var a = reg.Create("A", [M(1)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopA);
        var b = reg.Create("B", [M(2)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopB);

        reg.RehomeZoneFlocks(@"\\.\DISPLAY1", @"\\.\DISPLAY2", g => g.Name, DesktopA);

        Assert.Same(a, reg.ForZone(@"\\.\DISPLAY2", DesktopA));
        Assert.Same(b, reg.ForZone(@"\\.\DISPLAY1", DesktopB));
    }

    [Fact]
    public void Swapping_one_desktops_layouts_leaves_other_desktops_zone_flocks()
    {
        var reg = new FlockRegistry();
        var a1 = reg.Create("A1", [M(1)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopA);
        var a2 = reg.Create("A2", [M(2)], zoneMonitor: @"\\.\DISPLAY2", zoneDesktop: DesktopA);
        var b1 = reg.Create("B1", [M(3)], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: DesktopB);

        reg.SwapZoneFlocks(@"\\.\DISPLAY1", @"\\.\DISPLAY2", g => g.Name, DesktopA);

        Assert.Equal(@"\\.\DISPLAY2", a1.ZoneMonitor);
        Assert.Equal(@"\\.\DISPLAY1", a2.ZoneMonitor);
        Assert.Equal(@"\\.\DISPLAY1", b1.ZoneMonitor);
    }

    [Fact]
    public void Hand_picked_flocks_never_get_a_zone_desktop()
    {
        var reg = new FlockRegistry();
        var g = reg.Create("Hand", [M(1)], zoneDesktop: DesktopA);
        reg.SetZoneDesktop(g, DesktopB, "renamed");

        Assert.Null(g.ZoneDesktop);
        Assert.Equal("Hand", g.Name);
    }

    [Fact]
    public void Add_is_a_no_op_for_an_existing_member()
    {
        var reg = new FlockRegistry();
        var g = reg.Create("A", [M(1)]);
        var changes = 0;
        reg.Changed += () => changes++;

        reg.Add(g, M(1));
        Assert.Equal(0, changes);
    }
}
