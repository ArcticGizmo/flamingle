namespace Flamingle.Core.Geometry;

/// <summary>One display, in physical pixels.</summary>
/// <param name="Handle">The HMONITOR — valid only for this session.</param>
/// <param name="DeviceName">What per-monitor state (layouts, zone flocks, settings) is keyed on: the monitor's PnP
/// instance id (e.g. <c>DISPLAY\ACR03E3\8&amp;3157D11D&amp;0&amp;UID256</c>, see <see cref="MonitorIds"/>), which
/// survives the monitor reconnecting — else its GDI name.</param>
/// <param name="WorkArea">The monitor minus the taskbar and other app bars.</param>
/// <param name="Scale">DPI scale (1.0 = 96 dpi).</param>
/// <param name="GdiName">e.g. <c>\\.\DISPLAY1</c>. Renumbered whenever the monitor reconnects (a USB dock does it on
/// every wake), so only for showing — and for finding state saved under it by older versions.</param>
public sealed record MonitorInfo(
    long Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    double Scale,
    bool IsPrimary,
    string? GdiName = null)
{
    /// <summary>e.g. <c>"DISPLAY1"</c>.</summary>
    public string ShortName => MonitorIds.ShortName(GdiName ?? DeviceName);

    /// <summary>A friendly label, e.g. <c>"DISPLAY1 — 2560×1440 (primary)"</c>.</summary>
    public string Label => $"{ShortName} — {Bounds.Width}×{Bounds.Height}{(IsPrimary ? " (primary)" : "")}";

    public bool Contains(int x, int y) => x >= Bounds.X && x < Bounds.Right && y >= Bounds.Y && y < Bounds.Bottom;
}
