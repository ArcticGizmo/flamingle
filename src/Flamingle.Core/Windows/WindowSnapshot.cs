using Flamingle.Core.Geometry;

namespace Flamingle.Core.Windows;

/// <summary>
/// A point-in-time view of one top-level window, as the app's Win32 layer sees it. Handles are carried as
/// <see cref="long"/> so Core stays free of interop types and they serialise as plain numbers.
/// </summary>
/// <param name="Bounds">The visible frame (DWM extended frame bounds), in physical pixels.</param>
/// <param name="Monitor">An opaque per-monitor id (the HMONITOR) — only compared for equality.</param>
public sealed record WindowSnapshot(
    long Handle,
    uint ProcessId,
    string ProcessName,
    string Title,
    PixelRect Bounds,
    bool IsMinimised,
    bool IsMaximised,
    long Monitor);
