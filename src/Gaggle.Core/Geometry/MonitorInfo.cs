namespace Gaggle.Core.Geometry;

/// <summary>One display, in physical pixels.</summary>
/// <param name="Handle">The HMONITOR — valid only for this session.</param>
/// <param name="DeviceName">e.g. <c>\\.\DISPLAY1</c> — stable enough to key per-monitor settings on.</param>
/// <param name="WorkArea">The monitor minus the taskbar and other app bars.</param>
/// <param name="Scale">DPI scale (1.0 = 96 dpi).</param>
public sealed record MonitorInfo(
    long Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    double Scale,
    bool IsPrimary)
{
    /// <summary>e.g. <c>"DISPLAY1"</c>.</summary>
    public string ShortName => DeviceName.TrimStart('\\', '.');

    /// <summary>A friendly label, e.g. <c>"DISPLAY1 — 2560×1440 (primary)"</c>.</summary>
    public string Label => $"{ShortName} — {Bounds.Width}×{Bounds.Height}{(IsPrimary ? " (primary)" : "")}";

    public bool Contains(int x, int y) => x >= Bounds.X && x < Bounds.Right && y >= Bounds.Y && y < Bounds.Bottom;
}
