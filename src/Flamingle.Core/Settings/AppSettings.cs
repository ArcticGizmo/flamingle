using System.Text.Json.Serialization;

namespace Flamingle.Core.Settings;

/// <summary>
/// User settings, stored as <c>settings.json</c> in the data folder. Edited live from the settings window
/// (tray → "Settings…"); the file can also be edited by hand (restart to pick it up).
/// </summary>
public sealed record AppSettings
{
    /// <summary>Puts the layout on the monitor under the cursor into edit mode (its panel, split strips, and drag
    /// anywhere to move it) — or takes it out again.</summary>
    public string LayoutMenuHotkey { get; init; } = "Alt+Shift+E";

    /// <summary>Experimental: dragging a window to the top of a monitor drops down a strip of layouts — drop it on a
    /// zone to snap it there (switching the monitor to that layout if needed). Flamingle's take on Windows' snap
    /// bar; turn Windows' off to avoid getting both.</summary>
    public bool SnapStrip { get; init; } = true;

    /// <summary>Modifiers for the per-slot hotkeys: <c>{modifiers}+1</c> … <c>+9</c> toggle flock 1–9.
    /// Empty disables them.</summary>
    public string SlotHotkeyModifiers { get; init; } = "Alt+Shift";

    /// <summary>Experimental: minimising (or restoring) any one member — e.g. via its own title bar or the
    /// taskbar — takes the rest of its flock with it.</summary>
    public bool LinkedMinimise { get; init; } = true;

    /// <summary>Experimental: after a restore, put members back exactly where they were when minimised
    /// (in case an app restores its own geometry).</summary>
    public bool CorrectPositionsOnRestore { get; init; } = true;

    // ---- Zones (FancyZones-style drag-to-snap) ----

    public bool ZonesEnabled { get; init; } = true;

    /// <summary>Hold this while dragging a window to show the zones: <c>Shift</c>, <c>Alt</c>, or <c>None</c>
    /// (zones on every drag). Ctrl is always "span several zones".</summary>
    public string ZoneDragModifier { get; init; } = "Shift";

    /// <summary>Experimental: while dragging, show the zones on every monitor at once. Off: only on the monitor
    /// under the cursor (like FancyZones) — much cheaper on USB (DisplayLink) monitors, which redraw the whole screen
    /// in software whenever a full-screen overlay appears.</summary>
    public bool ZonesOnEveryMonitor { get; init; }

    /// <summary>Layout id per monitor key (<see cref="Geometry.MonitorInfo.DeviceName"/> → <c>columns-3</c>). Monitors
    /// not listed use <see cref="Zones.ZoneLayout.DefaultId"/>.</summary>
    public Dictionary<string, string> ZoneLayouts { get; init; } = [];

    /// <summary>Experimental: windows dropped into zones on a monitor join that monitor's zone flock, so the
    /// whole layout minimises / restores together.</summary>
    public bool ZonesFormFlock { get; init; } = true;

    /// <summary>Experimental: zone flocks are per monitor <i>per virtual desktop</i>, so zoned windows on
    /// different desktops never minimise / restore together. Off: one zone flock per monitor, across desktops.</summary>
    public bool ZoneFlockPerDesktop { get; init; } = true;

    /// <summary>Experimental: a window pulled out of its zones shrinks back to its pre-snap size as soon as the
    /// drag starts (like FancyZones / Windows Snap), rather than when it's dropped.</summary>
    public bool RestoreSizeDuringDrag { get; init; } = true;

    /// <summary>How far (physical px) a zoned window must be dragged from where it was grabbed before it
    /// shrinks back. Holding the zone modifier (Shift) before then keeps its zone size for the whole drag.</summary>
    public int RestoreSizeAfterPixels { get; init; } = 144;

    /// <summary>Experimental: resizing a zoned window by an inner edge moves that split, and the windows on the
    /// other side follow (like Windows 11 Snap Groups).</summary>
    public bool LinkedResize { get; init; } = true;

    /// <summary>Experimental: give every flock its own taskbar button (icon made from its apps' icons);
    /// clicking it minimises / restores the flock.</summary>
    public bool TaskbarButtons { get; init; } = true;

    /// <summary>Experimental: a flock's taskbar button moves to the virtual desktop most of its windows are on
    /// (otherwise it stays on the desktop it was created on).</summary>
    public bool TaskbarButtonFollowsDesktop { get; init; } = true;

    /// <summary>Experimental: what a flock's taskbar button looks like — <c>"Flamingle"</c> (the Flamingle icon,
    /// numbered with the monitor the flock is on when there's more than one) or <c>"Apps"</c> (a mosaic of its
    /// apps' icons).</summary>
    public string TaskbarIconStyle { get; init; } = "Flamingle";

    [JsonIgnore]
    public bool TaskbarIconShowsApps => string.Equals(TaskbarIconStyle, "Apps", StringComparison.OrdinalIgnoreCase);

    /// <summary>Experimental: a flock's taskbar button shows its own preview — the flock's windows laid out as they
    /// are on screen (pictures taken before a minimise stand in for minimised ones) — with Minimise / restore and
    /// Dissolve buttons under it. Off: Windows' default preview of the (empty) stand-in window.</summary>
    public bool TaskbarPreviews { get; init; } = true;

    /// <summary>Experimental: hovering a flock's taskbar button outlines its windows on screen. Needs
    /// <see cref="TaskbarPreviews"/> — the preview request is how Flamingle knows the button is hovered.</summary>
    public bool TaskbarHoverOutline { get; init; } = true;

    /// <summary>Experimental: editing a saved flock opens design mode — its layout drawn over the real screens, where
    /// you click a zone to choose what starts there, with a floating panel for whatever's selected. Off: the form in
    /// Settings › Flocks.</summary>
    public bool DesignModeEditing { get; init; } = true;

    // ---- Notifications ----

    /// <summary>Experimental: news that arrives in the background — update checks, a saved flock that didn't fully
    /// load, shortcuts another app already uses, a file that couldn't be written — goes to Windows notifications, which
    /// wait in the Notification Center. Off: the floating toast, like everything else.</summary>
    public bool WindowsNotifications { get; init; } = true;

    // ---- Changelog ----

    /// <summary>On the first launch after an update, pop a "what's new" window listing only the releases since
    /// <see cref="LastSeenVersion"/>. Its "Don't show changelogs again" button turns this off.</summary>
    public bool ShowChangelogOnUpdate { get; init; } = true;

    /// <summary>The version that last ran here (e.g. <c>0.1.0</c>), stamped at startup — how an update is spotted.
    /// Null on a fresh install, which is seeded silently (nothing to show).</summary>
    public string? LastSeenVersion { get; init; }

    public string LayoutIdFor(string deviceName)
        => ZoneLayouts.TryGetValue(deviceName, out var id) ? id : Zones.ZoneLayout.DefaultId;

    public AppSettings WithLayout(string deviceName, string layoutId)
        => this with { ZoneLayouts = new Dictionary<string, string>(ZoneLayouts) { [deviceName] = layoutId } };

    public static AppSettings Default { get; } = new();
}
