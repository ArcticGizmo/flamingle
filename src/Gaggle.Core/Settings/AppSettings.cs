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

    public static AppSettings Default { get; } = new();
}
