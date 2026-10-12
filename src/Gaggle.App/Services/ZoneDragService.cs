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

    // How far the cursor must travel after a mid-drag resize before we judge whether Windows kept it.
    private const int RestoreCheckDistance = 8;

    private Drag? _drag;

    // Windows whose drag we just restarted at their restored size — their next drag mustn't restore again.
    private readonly HashSet<IntPtr> _restarted = [];

    private sealed class Drag(IntPtr hwnd, PixelRect startRect, bool startedMaximised, Win32.POINT grab)
    {
        public IntPtr Hwnd { get; } = hwnd;
        public PixelRect StartRect { get; } = startRect;
        public bool StartedMaximised { get; } = startedMaximised;
        public Win32.POINT Grab { get; } = grab;   // cursor when the drag began
        public bool? IsResize { get; set; }   // unknown until the window first changes
        public bool Moved { get; set; }

        // Mid-drag size restore (see RestoreSizeOnce).
        public bool SizeRestored { get; set; }
        public PixelRect? Restore { get; set; }
        public PixelRect? RestoreTarget { get; set; }
        public Win32.POINT RestoreCheckFrom { get; set; }
        public bool Restarting { get; set; }

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
        Win32.GetCursorPos(out var grab);
        _drag = new Drag(hwnd, rect, Win32.IsZoomed(hwnd), grab)
        {
            // A drag we restarted ourselves is already at the restored size.
            SizeRestored = _restarted.Remove(hwnd),
        };
        _timer.Start();

        if (_containers.SlotOf(hwnd) is { } slot)
            DebugLog.Write($"drag start 0x{hwnd:X} {rect} slotted, restore={slot.Restore?.ToString() ?? "none"}, restarted={_drag.SizeRestored}");
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
        if (drag.IsResize == true || drag.Restarting) { ClearSelection(drag); return; }

        if (!Win32.GetCursorPos(out var cursor)) return;
        if (drag.IsResize == false)
        {
            CheckRestoreStuck(drag, cursor);
            RestoreSizeOnce(drag, cursor);
        }

        if (!ModifierHeld(_settings().ZoneDragModifier)) { ClearSelection(drag); return; }

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

    /// <summary>
    /// The first moment a zoned window is pulled away, shrink it back to its pre-snap size — under the cursor,
    /// keeping the grabbed point under the pointer — the way Windows Snap and FancyZones do. It stays in its
    /// layout until the drop: dropping it into zones again (Shift) just snaps it back.
    /// </summary>
    private void RestoreSizeOnce(Drag drag, Win32.POINT cursor)
    {
        if (drag.SizeRestored || !_settings().RestoreSizeDuringDrag) return;
        drag.SizeRestored = true;

        if (_containers.SlotOf(drag.Hwnd) is not { Restore: { } restore }) return;
        if (WindowQuery.WindowRect(drag.Hwnd) is not { } now) return;
        if (now.Width == restore.Width && now.Height == restore.Height) return;
        if (Win32.IsHungAppWindow(drag.Hwnd)) return;

        drag.Restore = restore;
        var r = ZoneSlot.RestoreUnderGrab(restore, drag.StartRect, drag.Grab.x, drag.Grab.y, cursor.x, cursor.y);
        // Synchronous so we can see straight away whether it took (the window's thread is pumping its drag loop).
        var ok = Win32.SetWindowPos(drag.Hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        DebugLog.Write($"restore mid-drag 0x{drag.Hwnd:X} → {r} ok={ok} now={WindowQuery.WindowRect(drag.Hwnd)}");

        drag.RestoreTarget = r;
        drag.RestoreCheckFrom = cursor;
    }

    /// <summary>
    /// Windows' drag loop may put the window back to the size it had when the drag began. Once the cursor has
    /// moved on a little, check: if the restored size didn't stick, restart the drag at the restored size.
    /// </summary>
    private void CheckRestoreStuck(Drag drag, Win32.POINT cursor)
    {
        if (drag.RestoreTarget is not { } target) return;
        if (Math.Abs(cursor.x - drag.RestoreCheckFrom.x) + Math.Abs(cursor.y - drag.RestoreCheckFrom.y) < RestoreCheckDistance)
            return;

        drag.RestoreTarget = null;
        var now = WindowQuery.WindowRect(drag.Hwnd);
        if (now is { } n && n.Width == target.Width && n.Height == target.Height)
        {
            DebugLog.Write($"restore held: {n}");
            return;
        }

        DebugLog.Write($"restore overridden by the drag loop (now {now}) — restarting the drag");
        RestartDrag(drag);
    }

    /// <summary>
    /// End Windows' drag loop (<c>WM_CANCELMODE</c>); when it reports the end, resize the window under the
    /// cursor and start a fresh drag loop (<c>SC_DRAGMOVE</c>) — the mouse button is still down, so the user
    /// just keeps dragging, now at the restored size. See <see cref="OnMoveSizeEnded"/>.
    /// </summary>
    private void RestartDrag(Drag drag)
    {
        drag.Restarting = true;
        HideOverlays();
        Win32.PostMessageW(drag.Hwnd, Win32.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);

        DispatcherTimer.RunOnce(() =>
        {
            if (_drag != drag || !drag.Restarting) return;
            DebugLog.Write("drag loop ignored WM_CANCELMODE — giving up on the mid-drag restore");
            drag.Restarting = false;
        }, TimeSpan.FromMilliseconds(400));
    }

    private void ContinueRestartedDrag(Drag drag)
    {
        var hwnd = drag.Hwnd;
        Win32.GetCursorPos(out var cursor);
        if (drag.Restore is not { } restore) return;

        var r = ZoneSlot.RestoreUnderGrab(restore, drag.StartRect, drag.Grab.x, drag.Grab.y, cursor.x, cursor.y);
        Win32.SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);

        if (Win32.IsKeyDown(Win32.VK_LBUTTON))
        {
            _restarted.Add(hwnd);
            var posted = Win32.PostMessageW(hwnd, Win32.WM_SYSCOMMAND, (IntPtr)Win32.SC_DRAGMOVE, Win32.MakeLParam(cursor.x, cursor.y));
            DebugLog.Write($"drag restarted at {r} posted={posted}");
        }
        else
        {
            // The button came up while we were switching over: treat it as the drop (out of the layout).
            DebugLog.Write($"button released during restart — dropped at {r}");
            _containers.Unslot(hwnd, cursor.x);
        }
    }

    private void OnMoveSizeEnded(IntPtr hwnd)
    {
        var drag = _drag;
        if (drag is null || drag.Hwnd != hwnd) return;

        if (drag.Restarting)
        {
            // Our own WM_CANCELMODE ended the loop — not a drop. Carry on at the restored size.
            EndDrag();
            ContinueRestartedDrag(drag);
            return;
        }

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
