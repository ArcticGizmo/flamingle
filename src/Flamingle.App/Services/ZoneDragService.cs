using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.App.Views;
using Flamingle.Core.Geometry;
using Flamingle.Core.Settings;
using Flamingle.Core.Zones;

namespace Flamingle.App.Services;

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

        // The snap strip: what's picked in it (null when nothing is).
        public StripPick? Strip { get; set; }
        public int StripAnchor { get; set; } = -1;
    }

    private sealed record StripPick(MonitorInfo Monitor, string LayoutId, IReadOnlyList<int> Selection);

    // The snap strip, and the zones it's previewing on screen.
    private SnapStripWindow? _strip;
    private MonitorInfo? _stripMonitor;
    private ZoneOverlayWindow? _stripOverlay;
    private string? _stripOverlayLayout;

    // How close to the top of a monitor's work area (DIP) a dragged window's cursor brings down the strip, and how
    // far around the strip (DIP) it can stray before the strip goes away.
    private const double StripTrigger = 12;
    private const double StripReach = 48;

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

    /// <summary>True while drags should be left alone (design mode: a window dragged onto a zone there is being added
    /// to a saved flock, not snapped).</summary>
    public Func<bool>? Paused { get; set; }

    private void OnMoveSizeStarted(IntPtr hwnd)
    {
        if (!_settings().ZonesEnabled || Paused?.Invoke() == true || !WindowQuery.IsCandidate(hwnd)) return;
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
        if (drag.IsResize == true)
        {
            // Resizing a zoned window by its edge moves its splits (if it's in a layout).
            ClearSelection(drag);
            _containers.ResizeTick(drag.Hwnd);
            return;
        }
        if (drag.Restarting) { ClearSelection(drag); return; }

        if (!Win32.GetCursorPos(out var cursor)) return;
        if (drag.IsResize == false)
        {
            CheckRestoreStuck(drag, cursor);
            MaybeRestoreSize(drag, cursor);
        }

        // The snap strip, unless the zone key is held (that's a deliberate zone drag).
        if (_settings().SnapStrip && drag.IsResize == false && !ZoneKeyHeld() && StripTick(drag, cursor))
        {
            drag.Selection = [];
            drag.SpanAnchor = -1;
            HideOverlays();
            return;
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
    /// <summary>
    /// Decide whether this drag is pulling the window out of its layout: only once it's travelled
    /// <see cref="AppSettings.RestoreSizeAfterPixels"/> from where it was grabbed. Holding the zone modifier
    /// before that means "moving between zones" — keep the zone size for the whole drag.
    /// </summary>
    private void MaybeRestoreSize(Drag drag, Win32.POINT cursor)
    {
        var s = _settings();
        if (drag.SizeRestored || !s.RestoreSizeDuringDrag) return;

        var modifierIsKey = !string.Equals(s.ZoneDragModifier?.Trim(), "none", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(s.ZoneDragModifier);
        if (modifierIsKey && ModifierHeld(s.ZoneDragModifier))
        {
            drag.SizeRestored = true;   // decided: no restore this drag
            if (_containers.SlotOf(drag.Hwnd) is not null)
                DebugLog.Write($"{s.ZoneDragModifier} held before the threshold — keeping the zone size");
            return;
        }

        var dx = cursor.x - drag.Grab.x;
        var dy = cursor.y - drag.Grab.y;
        var threshold = Math.Max(0, s.RestoreSizeAfterPixels);
        if (dx * dx + dy * dy < threshold * threshold) return;

        RestoreSizeOnce(drag, cursor);
    }

    private void RestoreSizeOnce(Drag drag, Win32.POINT cursor)
    {
        if (drag.SizeRestored) return;
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

        if (drag.IsResize == true)
        {
            _containers.ResizeEnded(hwnd);
            return;
        }
        if (!drag.Moved) return;
        if (drag.Strip is { } pick)
        {
            DropOnStrip(hwnd, drag, pick);
            return;
        }
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

    // ---- Snap strip ----

    /// <summary>
    /// One tick of the snap strip. Drops it down when the cursor reaches the top of a monitor; while it's down,
    /// tracks which zone of which layout the cursor is over (Ctrl spans, as with zones) and previews it full size.
    /// Returns true while the strip owns the drag (the cursor is on or near it).
    /// </summary>
    private bool StripTick(Drag drag, Win32.POINT cursor)
    {
        var monitor = drag.Monitors.FirstOrDefault(m => m.Contains(cursor.x, cursor.y));
        var nearTop = monitor is not null && cursor.y <= monitor.WorkArea.Y + StripTrigger * monitor.Scale;

        if (_strip is null)
        {
            if (!nearTop) return false;
            _stripMonitor = monitor!;
            _strip = new SnapStripWindow(monitor!, _containers.LayoutIdFor(monitor!));
            _strip.Show();
        }

        var m = _stripMonitor!;
        var reach = (int)(StripReach * m.Scale);
        var area = _strip.ScreenBounds;
        var onStrip = cursor.x >= area.X - reach && cursor.x < area.Right + reach
                      && cursor.y >= m.Bounds.Y && cursor.y < area.Bottom + reach;
        if (!onStrip && !(nearTop && monitor?.Handle == m.Handle))
        {
            HideStrip(drag);
            return false;
        }

        if (_strip.HitTest(cursor.x, cursor.y) is not { } hit)
        {
            // On the strip but between tiles: keep the last pick (so a drop in a gap still lands), just no new one.
            return true;
        }

        var zones = _containers.ZonesFor(m, hit.LayoutId);
        IReadOnlyList<int> selection;
        if (Win32.IsKeyDown(Win32.VK_CONTROL) && drag.Strip?.LayoutId == hit.LayoutId)
        {
            if (drag.StripAnchor < 0) drag.StripAnchor = hit.Zone;
            selection = ZoneMath.Span(zones, drag.StripAnchor, hit.Zone);
        }
        else
        {
            drag.StripAnchor = Win32.IsKeyDown(Win32.VK_CONTROL) ? hit.Zone : -1;
            selection = [hit.Zone];
        }

        drag.Strip = new StripPick(m, hit.LayoutId, selection);
        _strip.Highlight(hit.LayoutId, selection);
        PreviewStripPick(m, hit.LayoutId, zones, selection);
        return true;
    }

    /// <summary>Show the picked layout's zones full size on the monitor, with the picked one(s) lit.</summary>
    private void PreviewStripPick(MonitorInfo monitor, string layoutId, IReadOnlyList<PixelRect> zones, IReadOnlyList<int> selection)
    {
        if (_stripOverlay is null || _stripOverlayLayout != layoutId)
        {
            _stripOverlay?.Close();
            _stripOverlay = new ZoneOverlayWindow(monitor, zones, ZoneLayout.ById(layoutId).Name);
            _stripOverlayLayout = layoutId;
            _stripOverlay.Show();
            // The strip stays on top of its own preview (raised without activation — the drag must keep focus).
            if (_strip?.TryGetPlatformHandle()?.Handle is { } strip)
                Win32.SetWindowPos(strip, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        _stripOverlay.Highlight(selection.ToList());
    }

    private void HideStrip(Drag? drag)
    {
        if (drag is not null)
        {
            drag.Strip = null;
            drag.StripAnchor = -1;
        }
        _strip?.Close();
        _strip = null;
        _stripMonitor = null;
        _stripOverlay?.Close();
        _stripOverlay = null;
        _stripOverlayLayout = null;
    }

    /// <summary>Dropped on the strip: switch the monitor to that layout if it isn't already, then snap the window
    /// into the picked zone(s) — exactly like a zone drop from there on.</summary>
    private void DropOnStrip(IntPtr hwnd, Drag drag, StripPick pick)
    {
        if (pick.LayoutId != _containers.LayoutIdFor(pick.Monitor))
            _containers.PickLayout(pick.Monitor, pick.LayoutId);

        var zones = _containers.ZonesFor(pick.Monitor);
        var target = ZoneMath.Bounds(zones, pick.Selection);
        WindowPlacer.Fit(hwnd, target);
        // Windows may apply its own edge-snap (maximise at the top edge) after the drop; re-fit once it settles.
        DispatcherTimer.RunOnce(() => WindowPlacer.Fit(hwnd, target), TimeSpan.FromMilliseconds(120));
        _containers.Snapped(hwnd, pick.Monitor, pick.Selection, drag.StartedMaximised ? null : drag.StartRect);
    }

    private bool ZoneKeyHeld()
    {
        var key = (_settings().ZoneDragModifier ?? "").Trim();
        return !string.Equals(key, "none", StringComparison.OrdinalIgnoreCase) && key.Length > 0 && ModifierHeld(key);
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
        HideStrip(null);
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
