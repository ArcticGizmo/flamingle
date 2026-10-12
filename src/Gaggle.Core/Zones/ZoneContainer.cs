using Gaggle.Core.Geometry;

namespace Gaggle.Core.Zones;

/// <summary>Which zone(s) one window sits in. <see cref="ProcessId"/> guards against a recycled handle.</summary>
/// <param name="Restore">The window rect from before it was first snapped (null if it was maximised) — it goes
/// back to this size when dragged out of the layout, and to this place too when the layout releases it.</param>
public sealed record ZoneSlot(long Handle, uint ProcessId, List<int> Zones, PixelRect? Restore = null)
{
    /// <summary>
    /// Where a window dragged out of its zones should end up: its pre-snap size, placed so the cursor keeps
    /// the same relative spot along the title bar (so the window doesn't jump out from under the pointer).
    /// </summary>
    public static PixelRect RestoreAtCursor(PixelRect restore, PixelRect current, int cursorX)
    {
        var fraction = current.Width > 0 ? Math.Clamp((cursorX - current.X) / (double)current.Width, 0, 1) : 0.5;
        var x = cursorX - (int)Math.Round(fraction * restore.Width);
        return new PixelRect(x, current.Y, restore.Width, restore.Height);
    }

    /// <summary>
    /// Mid-drag: the pre-snap size, placed under the cursor <em>now</em> so the point the user grabbed (at
    /// <paramref name="grabX"/>, <paramref name="grabY"/> on <paramref name="grabbed"/>) stays under the pointer —
    /// horizontally as the same fraction of the width, vertically the same distance below the top (so the
    /// title bar stays under the pointer).
    /// </summary>
    public static PixelRect RestoreUnderGrab(PixelRect restore, PixelRect grabbed, int grabX, int grabY, int cursorX, int cursorY)
    {
        var fraction = grabbed.Width > 0 ? Math.Clamp((grabX - grabbed.X) / (double)grabbed.Width, 0, 1) : 0.5;
        var below = Math.Clamp(grabY - grabbed.Y, 0, Math.Max(0, restore.Height - 1));
        return new PixelRect(
            cursorX - (int)Math.Round(fraction * restore.Width),
            cursorY - below,
            restore.Width,
            restore.Height);
    }
}

/// <summary>
/// A zone layout as a movable container: a layout, the frame it's laid out in (physical px — the monitor's
/// work area by default, but the user can move / resize it), and the windows snapped into its zones. Moving
/// the frame moves every slotted window with it. One container per monitor.
/// </summary>
public sealed class ZoneContainer
{
    /// <summary>The smallest a frame can be resized to (physical px).</summary>
    public const int MinWidth = 480;
    public const int MinHeight = 320;

    public ZoneContainer(string deviceName, string layoutId, PixelRect frame)
    {
        DeviceName = deviceName;
        LayoutId = layoutId;
        Frame = frame;
    }

    /// <summary>The monitor the container currently lives on.</summary>
    public string DeviceName { get; set; }

    /// <summary>The preset. Setting it clears any custom splits.</summary>
    public string LayoutId
    {
        get => _layoutId;
        set
        {
            _layoutId = value;
            CustomZones = null;
        }
    }

    private string _layoutId = "";

    /// <summary>The preset's zones after the user dragged its dividers, or null for the preset as-is.</summary>
    public IReadOnlyList<ZoneFraction>? CustomZones { get; set; }

    public PixelRect Frame { get; set; }

    public Dictionary<long, ZoneSlot> Slots { get; } = [];

    /// <summary>The preset, with any custom splits applied.</summary>
    public ZoneLayout Layout
    {
        get
        {
            var preset = ZoneLayout.ById(LayoutId);
            // Custom splits only make sense for the preset they were made from (same zone count).
            return CustomZones is { } custom && custom.Count == preset.Zones.Count ? preset with { Zones = custom } : preset;
        }
    }

    public IReadOnlyList<PixelRect> Zones(int spacing) => ZoneMath.Resolve(Layout, Frame, spacing);

    public IReadOnlyList<ZoneDivider> Dividers() => ZoneDividers.Find(Layout.Zones);

    /// <summary>Where a slot's window belongs for the given zones. Indices past the end (after switching to
    /// a layout with fewer zones) fall back to the last zone.</summary>
    public static PixelRect TargetFor(ZoneSlot slot, IReadOnlyList<PixelRect> zones)
    {
        if (zones.Count == 0) return default;
        var valid = slot.Zones.Select(i => Math.Clamp(i, 0, zones.Count - 1)).Distinct().ToList();
        return ZoneMath.Bounds(zones, valid.Count > 0 ? valid : [0]);
    }

    /// <summary>
    /// Fit <paramref name="frame"/> inside <paramref name="workArea"/>: shrink it if it's too big, then slide
    /// it fully on-screen. Never smaller than the minimum (unless the work area itself is).
    /// </summary>
    public static PixelRect ClampInto(PixelRect frame, PixelRect workArea)
    {
        var w = Math.Clamp(frame.Width, Math.Min(MinWidth, workArea.Width), workArea.Width);
        var h = Math.Clamp(frame.Height, Math.Min(MinHeight, workArea.Height), workArea.Height);
        var x = Math.Clamp(frame.X, workArea.X, workArea.Right - w);
        var y = Math.Clamp(frame.Y, workArea.Y, workArea.Bottom - h);
        return new PixelRect(x, y, w, h);
    }

    /// <summary>Move the frame by an offset (no clamping — that happens on drop).</summary>
    public static PixelRect Offset(PixelRect frame, int dx, int dy) => frame with { X = frame.X + dx, Y = frame.Y + dy };
}

/// <summary>The on-disk form of the containers (<c>zones.json</c>).</summary>
public sealed record ZoneContainerState(
    string DeviceName, string LayoutId, PixelRect Frame, List<ZoneSlot> Slots, List<ZoneFraction>? CustomZones = null)
{
    public static ZoneContainerState From(ZoneContainer c)
        => new(c.DeviceName, c.LayoutId, c.Frame, [.. c.Slots.Values], c.CustomZones?.ToList());

    public ZoneContainer ToContainer(Func<ZoneSlot, bool> isAlive)
    {
        var c = new ZoneContainer(DeviceName, LayoutId, Frame) { CustomZones = CustomZones };
        foreach (var s in Slots.Where(isAlive)) c.Slots[s.Handle] = s;
        return c;
    }
}
