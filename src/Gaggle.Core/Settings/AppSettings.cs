namespace Gaggle.Core.Settings;

/// <summary>
/// User settings, stored as <c>settings.json</c> in the data folder. No settings UI yet — edit the file
/// (tray → "Open data folder") and restart, or flip the experimental toggles from the tray menu.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Opens the picker, pre-checking the windows snapped with the focused one.</summary>
    public string NewGaggleHotkey { get; init; } = "Alt+Shift+G";

    /// <summary>Minimise / restore the gaggle holding the focused window (else the last-used gaggle).</summary>
    public string ToggleHotkey { get; init; } = "Alt+Shift+M";

    /// <summary>Modifiers for the per-slot hotkeys: <c>{modifiers}+1</c> … <c>+9</c> toggle gaggle 1–9.
    /// Empty disables them.</summary>
    public string SlotHotkeyModifiers { get; init; } = "Alt+Shift";

    /// <summary>Experimental: minimising (or restoring) any one member — e.g. via its own title bar or the
    /// taskbar — takes the rest of its gaggle with it.</summary>
    public bool LinkedMinimise { get; init; } = true;

    /// <summary>Experimental: after a restore, put members back exactly where they were when minimised
    /// (in case an app restores its own geometry).</summary>
    public bool CorrectPositionsOnRestore { get; init; } = true;

    // ---- Zones (FancyZones-style drag-to-snap) ----

    public bool ZonesEnabled { get; init; } = true;

    /// <summary>Hold this while dragging a window to show the zones: <c>Shift</c>, <c>Alt</c>, or <c>None</c>
    /// (zones on every drag). Ctrl is always "span several zones".</summary>
    public string ZoneDragModifier { get; init; } = "Shift";

    /// <summary>Gap between zones (and around the edge of the work area), in physical pixels.</summary>
    public int ZoneSpacing { get; init; } = 8;

    /// <summary>Gaps on/off (quick toggle in the layout tab). Off lays zones flush, keeping
    /// <see cref="ZoneSpacing"/> for when they're switched back on.</summary>
    public bool ZoneGaps { get; init; } = true;

    /// <summary>The spacing actually used: <see cref="ZoneSpacing"/>, or 0 with gaps off.</summary>
    public int EffectiveZoneSpacing => ZoneGaps ? Math.Max(0, ZoneSpacing) : 0;

    /// <summary>Layout id per monitor device name (e.g. <c>\\.\DISPLAY1</c> → <c>columns-3</c>). Monitors not
    /// listed use <see cref="Zones.ZoneLayout.DefaultId"/>.</summary>
    public Dictionary<string, string> ZoneLayouts { get; init; } = [];

    /// <summary>Experimental: windows dropped into zones on a monitor join that monitor's zone gaggle, so the
    /// whole layout minimises / restores together.</summary>
    public bool ZonesFormGaggle { get; init; } = true;

    /// <summary>Experimental: a window pulled out of its zones shrinks back to its pre-snap size as soon as the
    /// drag starts (like FancyZones / Windows Snap), rather than when it's dropped.</summary>
    public bool RestoreSizeDuringDrag { get; init; } = true;

    /// <summary>Experimental: resizing a zoned window by an inner edge moves that split, and the windows on the
    /// other side follow (like Windows 11 Snap Groups).</summary>
    public bool LinkedResize { get; init; } = true;

    /// <summary>Experimental: give every gaggle its own taskbar button (icon made from its apps' icons);
    /// clicking it minimises / restores the gaggle.</summary>
    public bool TaskbarButtons { get; init; } = true;

    public string LayoutIdFor(string deviceName)
        => ZoneLayouts.TryGetValue(deviceName, out var id) ? id : Zones.ZoneLayout.DefaultId;

    public AppSettings WithLayout(string deviceName, string layoutId)
        => this with { ZoneLayouts = new Dictionary<string, string>(ZoneLayouts) { [deviceName] = layoutId } };

    public static AppSettings Default { get; } = new();
}
