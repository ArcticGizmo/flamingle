using Avalonia.Threading;
using Gaggle.App.Native;
using Gaggle.App.Views;
using Gaggle.Core.Geometry;
using Gaggle.Core.Settings;
using Gaggle.Core.Zones;

namespace Gaggle.App.Services;

/// <summary>
/// FancyZones-style drag-to-snap. When the user starts dragging any app window (a WinEvent
/// <c>MOVESIZESTART</c>), this polls the cursor and modifier keys at ~60 Hz. While the zone modifier is
/// held (Shift by default), every monitor shows its zones and the one under the cursor lights up; holding
/// Ctrl as well spans from the zone where Ctrl went down to the current one. On drop (<c>MOVESIZEEND</c>)
/// the window is fitted to the highlighted zone(s). Resizes are recognised and ignored.
/// </summary>
internal sealed class ZoneDragService : IDisposable
{
    private readonly WinEventHooks _hooks;
    private readonly Func<AppSettings> _settings;
    private readonly ZoneContainerService _containers;
    private readonly DispatcherTimer _timer;
    private readonly List<ZoneOverlayWindow> _overlays = [];

    private Drag? _drag;

    private sealed class Drag(IntPtr hwnd, PixelRect startRect, bool startedMaximised)
    {
        public IntPtr Hwnd { get; } = hwnd;
        public PixelRect StartRect { get; } = startRect;
        public bool StartedMaximised { get; } = startedMaximised;
        public bool? IsResize { get; set; }   // unknown until the window first changes
        public bool Moved { get; set; }
        public List<MonitorInfo> Monitors { get; } = Native.Monitors.All();
        public MonitorInfo? Monitor { get; set; }
        public IReadOnlyList<PixelRect> Zones { get; set; } = [];
        public int SpanAnchor { get; set; } = -1;
        public IReadOnlyList<int> Selection { get; set; } = [];
    }

    public ZoneDragService(WinEventHooks hooks, Func<AppSettings> settings, ZoneContainerService containers)
    {
        _hooks = hooks;
        _settings = settings;
        _containers = containers;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        _hooks.MoveSizeStarted += OnMoveSizeStarted;
        _hooks.MoveSizeEnded += OnMoveSizeEnded;
    }

    /// <summary>Flash a monitor's zones for a moment — used when the user picks a new layout.</summary>
    public void Preview(MonitorInfo monitor)
    {
        if (_drag is not null) return;
        HideOverlays();
        var overlay = new ZoneOverlayWindow(monitor, _containers.ZonesFor(monitor), LayoutCaption(monitor));
        overlay.Show();
        _overlays.Add(overlay);
        DispatcherTimer.RunOnce(() => { if (_drag is null) HideOverlays(); }, TimeSpan.FromMilliseconds(1400));
    }

    private void OnMoveSizeStarted(IntPtr hwnd)
    {
        if (!_settings().ZonesEnabled || !WindowQuery.IsCandidate(hwnd)) return;
        if (WindowQuery.WindowRect(hwnd) is not { } rect) return;

        HideOverlays();
        _drag = new Drag(hwnd, rect, Win32.IsZoomed(hwnd));
        _timer.Start();
    }

    private void Tick()
    {
        var drag = _drag;
        if (drag is null) { _timer.Stop(); return; }
        if (!Win32.IsWindow(drag.Hwnd)) { EndDrag(); return; }

        // Tell a move from a resize the first time the window changes: a move keeps its size (except a
        // maximised window, which un-maximises as it's pulled away).
        if (WindowQuery.WindowRect(drag.Hwnd) is { } now && now != drag.StartRect)
        {
            drag.Moved = true;
            drag.IsResize ??= !drag.StartedMaximised
                && (now.Width != drag.StartRect.Width || now.Height != drag.StartRect.Height);
        }
        if (drag.IsResize == true) { ClearSelection(drag); return; }

        if (!ModifierHeld(_settings().ZoneDragModifier)) { ClearSelection(drag); return; }

        if (!Win32.GetCursorPos(out var cursor)) return;
        var monitor = drag.Monitors.FirstOrDefault(m => m.Contains(cursor.x, cursor.y));
        if (monitor is null) { ClearSelection(drag); return; }

        if (_overlays.Count == 0) ShowOverlays(drag);
        if (drag.Monitor?.Handle != monitor.Handle)
        {
            drag.Monitor = monitor;
            drag.Zones = _containers.ZonesFor(monitor);
            drag.SpanAnchor = -1;
        }

        var hit = ZoneMath.HitTest(drag.Zones, cursor.x, cursor.y, monitor.WorkArea);
        if (Win32.IsKeyDown(Win32.VK_CONTROL))
        {
            if (drag.SpanAnchor < 0) drag.SpanAnchor = hit;
            drag.Selection = ZoneMath.Span(drag.Zones, drag.SpanAnchor, hit);
        }
        else
        {
            drag.SpanAnchor = -1;
            drag.Selection = hit >= 0 ? [hit] : [];
        }

        foreach (var o in _overlays)
            o.Highlight(o.MonitorHandle == monitor.Handle ? drag.Selection.ToList() : []);
    }

    private void OnMoveSizeEnded(IntPtr hwnd)
    {
        var drag = _drag;
        if (drag is null || drag.Hwnd != hwnd) return;

        // One last sample, so a quick flick that ends before the next tick still lands where the cursor is.
        Tick();
        EndDrag();

        if (drag.IsResize == true || !drag.Moved) return;
        if (drag.Selection.Count > 0 && drag.Monitor is { } monitor)
        {
            var target = ZoneMath.Bounds(drag.Zones, drag.Selection);
            WindowPlacer.Fit(hwnd, target);
            // Windows may apply its own edge-snap after the drop; re-fit once things settle.
            DispatcherTimer.RunOnce(() => WindowPlacer.Fit(hwnd, target), TimeSpan.FromMilliseconds(120));
            _containers.Snapped(hwnd, monitor, drag.Selection, drag.StartedMaximised ? null : drag.StartRect);
        }
        else
        {
            // Dragged and dropped without snapping — e.g. pulled out of its zone.
            // Back to its pre-snap size, under the cursor.
            Win32.GetCursorPos(out var cursor);
            _containers.Unslot(hwnd, cursor.x);
        }
    }

    private void ShowOverlays(Drag drag)
    {
        foreach (var m in drag.Monitors)
        {
            var overlay = new ZoneOverlayWindow(m, _containers.ZonesFor(m), LayoutCaption(m));
            overlay.Show();
            _overlays.Add(overlay);
        }
    }

    private string LayoutCaption(MonitorInfo monitor)
        => $"{ZoneLayout.ById(_containers.LayoutIdFor(monitor)).Name}  ·  add Ctrl to stretch across several zones";

    private void ClearSelection(Drag drag)
    {
        drag.Selection = [];
        drag.SpanAnchor = -1;
        HideOverlays();
    }

    private void EndDrag()
    {
        _timer.Stop();
        _drag = null;
        HideOverlays();
    }

    private void HideOverlays()
    {
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
    }

    private static bool ModifierHeld(string? modifier) => (modifier ?? "").Trim().ToLowerInvariant() switch
    {
        "none" or "" => true,
        "alt" => Win32.IsKeyDown(Win32.VK_MENU),
        "ctrl" or "control" => Win32.IsKeyDown(Win32.VK_CONTROL),
        _ => Win32.IsKeyDown(Win32.VK_SHIFT),
    };

    public void Dispose()
    {
        _timer.Stop();
        HideOverlays();
    }
}
