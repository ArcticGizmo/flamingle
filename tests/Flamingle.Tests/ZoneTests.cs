using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Zones;

namespace Flamingle.Tests;

public class ZoneMathTests
{
    private static readonly PixelRect Work = new(0, 0, 1920, 1040);

    [Fact]
    public void Two_columns_without_spacing_split_the_work_area_exactly()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), Work, spacing: 0);
        Assert.Equal([new PixelRect(0, 0, 960, 1040), new PixelRect(960, 0, 960, 1040)], zones);
    }

    [Fact]
    public void Spacing_insets_the_edges_and_separates_neighbours()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), Work, spacing: 8);
        Assert.Equal(PixelRect.FromEdges(8, 8, 956, 1032), zones[0]);
        Assert.Equal(PixelRect.FromEdges(964, 8, 1912, 1032), zones[1]);
        Assert.Equal(8, zones[1].X - zones[0].Right);
    }

    [Fact]
    public void Resolves_relative_to_a_non_zero_work_area()
    {
        var work = new PixelRect(-1920, 40, 1920, 1000);
        var zones = ZoneMath.Resolve(ZoneLayout.ById("rows-2"), work, spacing: 0);
        Assert.Equal([new PixelRect(-1920, 40, 1920, 500), new PixelRect(-1920, 540, 1920, 500)], zones);
    }

    [Fact]
    public void Three_columns_tile_without_pixel_gaps()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), new PixelRect(0, 0, 1000, 500), spacing: 0);
        Assert.Equal(zones[0].Right, zones[1].X);
        Assert.Equal(zones[1].Right, zones[2].X);
        Assert.Equal(1000, zones[2].Right);
    }

    [Fact]
    public void Hit_test_finds_the_zone_and_snaps_gutters_to_the_nearest()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), Work, spacing: 8);
        Assert.Equal(0, ZoneMath.HitTest(zones, 100, 500, Work));
        Assert.Equal(1, ZoneMath.HitTest(zones, 1500, 500, Work));
        Assert.Equal(1, ZoneMath.HitTest(zones, 962, 500, Work));   // in the gutter, nearer the right
        Assert.Equal(0, ZoneMath.HitTest(zones, 2, 2, Work));        // in the outer margin
        Assert.Equal(-1, ZoneMath.HitTest(zones, 2000, 500, Work));  // off the work area
    }

    [Fact]
    public void Span_covers_everything_between_the_two_zones()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("grid-2x3"), Work, spacing: 8);
        // Top-left (0) to bottom-middle (4) → columns 0–1 on both rows.
        Assert.Equal([0, 1, 3, 4], ZoneMath.Span(zones, 0, 4));
        Assert.Equal([2], ZoneMath.Span(zones, 2, 2));
        Assert.Equal([5], ZoneMath.Span(zones, -1, 5));
    }

    [Fact]
    public void Bounds_of_a_span_runs_edge_to_edge()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), Work, spacing: 0);
        Assert.Equal(new PixelRect(0, 0, 1280, 1040), ZoneMath.Bounds(zones, [0, 1]));
    }

    [Fact]
    public void Occupied_recognises_a_window_filling_zones()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), Work, spacing: 0);
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
        Assert.Equal([new PixelRect(100, 50, 500, 600), new PixelRect(600, 50, 500, 600)], c.Zones(spacing: 0));

        c.Frame = ZoneContainer.Offset(c.Frame, 200, 100);
        Assert.Equal(new PixelRect(300, 150, 500, 600), c.Zones(spacing: 0)[0]);
    }

    [Fact]
    public void Target_covers_every_zone_in_the_slot()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-3"), Work, spacing: 0);
        var slot = new ZoneSlot(1, 1, [0, 1]);
        Assert.Equal(new PixelRect(0, 0, 1280, 1040), ZoneContainer.TargetFor(slot, zones));
    }

    [Fact]
    public void Target_falls_back_to_the_last_zone_after_switching_to_fewer_zones()
    {
        var zones = ZoneMath.Resolve(ZoneLayout.ById("columns-2"), Work, spacing: 0);
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
    public void Custom_splits_apply_and_reset_with_a_new_preset()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-2", new PixelRect(0, 0, 1000, 500));
        c.CustomZones = [new ZoneFraction(0, 0, 0.7, 1), new ZoneFraction(0.7, 0, 0.3, 1)];
        Assert.Equal(new PixelRect(0, 0, 700, 500), c.Zones(spacing: 0)[0]);

        c.LayoutId = "columns-2";   // picking a preset (even the same one) evens it out
        Assert.Null(c.CustomZones);
        Assert.Equal(new PixelRect(0, 0, 500, 500), c.Zones(spacing: 0)[0]);
    }

    [Fact]
    public void Custom_splits_for_a_different_zone_count_are_ignored()
    {
        var c = new ZoneContainer(@"\\.\DISPLAY1", "columns-3", new PixelRect(0, 0, 900, 500))
        {
            CustomZones = [new ZoneFraction(0, 0, 0.7, 1), new ZoneFraction(0.7, 0, 0.3, 1)],
        };
        Assert.Equal(3, c.Zones(spacing: 0).Count);
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
}

public class ZoneFlockRegistryTests
{
    private static FlockMember M(long handle) => new(handle, 1, $"app{handle}", $"Window {handle}");

    [Fact]
    public void Add_moves_a_window_into_an_existing_flock()
    {
        var reg = new FlockRegistry();
        var hand = reg.Create("Hand", [M(1), M(2)]);
        var zones = reg.Create("Zones", [M(3)], zoneMonitor: @"\\.\DISPLAY1");

        reg.Add(zones, M(2));

        Assert.Equal([1L], hand.Members.Select(m => m.Handle));
        Assert.Equal([3L, 2L], zones.Members.Select(m => m.Handle));
        Assert.Same(zones, reg.ForZoneMonitor(@"\\.\DISPLAY1"));
        Assert.Null(reg.ForZoneMonitor(@"\\.\DISPLAY2"));
    }

    [Fact]
    public void Rehoming_moves_and_renames_the_zone_flock()
    {
        var reg = new FlockRegistry();
        var g = reg.Create("Zones · DISPLAY1", [M(1)], zoneMonitor: @"\\.\DISPLAY1");

        reg.RehomeZoneFlock(@"\\.\DISPLAY1", @"\\.\DISPLAY2", "Zones · DISPLAY2");

        Assert.Same(g, reg.ForZoneMonitor(@"\\.\DISPLAY2"));
        Assert.Null(reg.ForZoneMonitor(@"\\.\DISPLAY1"));
        Assert.Equal("Zones · DISPLAY2", g.Name);
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
