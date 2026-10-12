using Avalonia;
using Avalonia.Threading;
using Gaggle.App.Native;
using Gaggle.App.Views;
using Gaggle.Core.Geometry;
using Gaggle.Core.Persistence;
using Gaggle.Core.Settings;
using Gaggle.Core.Zones;
using PixelRect = Gaggle.Core.Geometry.PixelRect;

namespace Gaggle.App.Services;

/// <summary>
/// Owns the zone containers — one per monitor, each a layout laid out in a movable frame, plus the windows
/// snapped into its zones — and their handles: a chip on the top edge (drag to move, click for presets and
/// actions) and a resize grip. Moving a frame re-places every slotted window in one <c>DeferWindowPos</c>
/// batch, so the layout travels as a unit.
///
/// <para>The handles show whenever the layout has windows on screen. They aren't topmost: they're lifted just
/// above the layout when one of its windows comes to the front, so another app in front covers them too.</para>
/// </summary>
internal sealed class ZoneContainerService : IDisposable
{
    private const uint RaiseFlags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;

    private readonly Func<AppSettings> _settings;
    private readonly WinEventHooks _hooks;
    private readonly JsonFile<List<ZoneContainerState>> _file;
    private readonly List<ZoneContainer> _containers = [];
    private readonly Dictionary<ZoneContainer, Handles> _handles = [];
    private readonly DispatcherTimer _sync;
    private readonly DispatcherTimer _apply;

    private DragState? _drag;

    private sealed record Handles(ContainerHandleWindow Chip, ContainerHandleWindow Grip);

    private sealed class DragState(ZoneContainer container, ContainerHandleWindow.Kind kind, Dictionary<long, WindowPlacer.Margins> margins)
    {
        public ZoneContainer Container { get; } = container;
        public ContainerHandleWindow.Kind Kind { get; } = kind;
        public PixelRect StartFrame { get; } = container.Frame;
        public string StartDevice { get; } = container.DeviceName;
        public Dictionary<long, WindowPlacer.Margins> Margins { get; } = margins;
        public bool Pending { get; set; }
    }

    public ZoneContainerService(Func<AppSettings> settings, WinEventHooks hooks, string path)
    {
        _settings = settings;
        _hooks = hooks;
        _file = new JsonFile<List<ZoneContainerState>>(path);

        _sync = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _sync.Tick += (_, _) => SyncHandles();

        // Window moves during a handle drag are coalesced to one batch per frame.
        _apply = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _apply.Tick += (_, _) => ApplyPendingDrag();
    }

    /// <summary>A window was put into a layout's zones (by drag, or "fill with open windows").</summary>
    public event Action<IntPtr, MonitorInfo>? Slotted;

    /// <summary>A window left its layout (dragged out, or released).</summary>
    public event Action<IntPtr>? Unslotted;

    /// <summary>A container moved to another monitor (from device, to monitor) — its zone gaggle follows.</summary>
    public event Action<string, MonitorInfo>? Rehomed;

    /// <summary>The chip's "Minimise layout" (device name of the layout's monitor).</summary>
    public event Action<string>? MinimiseRequested;

    /// <summary>A layout preset was picked from a chip (monitor, layout id).</summary>
    public event Action<MonitorInfo, string>? LayoutPicked;

    public event Action<string>? Notice;

    public void Start()
    {
        var monitors = Monitors.All();
        foreach (var state in _file.Load([]))
        {
            var monitor = monitors.FirstOrDefault(m => m.DeviceName == state.DeviceName);
            if (monitor is null) continue;   // that monitor's gone; forget the container
            var c = state.ToContainer(IsAlive);
            c.Frame = ZoneContainer.ClampInto(c.Frame, monitor.WorkArea);
            _containers.Add(c);
        }
        Save();

        _hooks.ForegroundChanged += OnForegroundChanged;
        _sync.Start();
    }

    // ---- Queries used by the drag-to-snap overlay ----

    public ZoneContainer? ContainerOn(string deviceName) => _containers.FirstOrDefault(c => c.DeviceName == deviceName);

    public string LayoutIdFor(MonitorInfo monitor)
        => ContainerOn(monitor.DeviceName)?.LayoutId ?? _settings().LayoutIdFor(monitor.DeviceName);

    /// <summary>The zones for a monitor: its container's (in the container's frame) or the default layout
    /// filling the work area.</summary>
    public IReadOnlyList<PixelRect> ZonesFor(MonitorInfo monitor)
    {
        var spacing = _settings().ZoneSpacing;
        return ContainerOn(monitor.DeviceName)?.Zones(spacing)
            ?? ZoneMath.Resolve(ZoneLayout.ById(LayoutIdFor(monitor)), monitor.WorkArea, spacing);
    }

    // ---- Membership ----

    /// <summary>A window was dropped into <paramref name="zones"/> on <paramref name="monitor"/>.</summary>
    /// <param name="before">The window's rect before this drag (null if it was maximised).</param>
    public void Snapped(IntPtr hwnd, MonitorInfo monitor, IReadOnlyList<int> zones, PixelRect? before)
    {
        // Moving between zones keeps the size from before the *first* snap.
        var previous = RemoveSlot(hwnd);
        Slot(GetOrCreate(monitor), hwnd, zones, previous is not null ? previous.Restore : before);
        Slotted?.Invoke(hwnd, monitor);
        Save();
        SyncHandles();
    }

    /// <summary>A window was dragged out of its zones and dropped at <paramref name="cursorX"/>: it leaves the
    /// layout and goes back to its pre-snap size under the cursor.</summary>
    public void Unslot(IntPtr hwnd, int cursorX)
    {
        if (RemoveSlot(hwnd) is not { } slot) return;

        if (slot.Restore is { } restore && !Win32.IsZoomed(hwnd) && WindowQuery.WindowRect(hwnd) is { } current)
        {
            var r = ZoneSlot.RestoreAtCursor(restore, current, cursorX);
            Win32.SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height,
                Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
        }

        Unslotted?.Invoke(hwnd);
        Save();
    }

    /// <summary>Switch a monitor's layout. Windows already in its zones re-flow into the new ones.</summary>
    public void SetLayout(MonitorInfo monitor, string layoutId)
    {
        if (ContainerOn(monitor.DeviceName) is not { } c) return;
        c.LayoutId = layoutId;
        Reflow(c);
        Save();
        SyncHandles();
    }

    /// <summary>
    /// Drop the monitor's open windows into the layout's free zones — topmost window into the first free
    /// zone, and so on — so a layout can be filled in one go rather than a drag per window.
    /// </summary>
    public void FillWithOpenWindows(MonitorInfo monitor)
    {
        var c = GetOrCreate(monitor);
        Prune();
        var zones = c.Zones(_settings().ZoneSpacing);

        var taken = c.Slots.Values.SelectMany(s => s.Zones).ToHashSet();
        var free = Enumerable.Range(0, zones.Count).Where(i => !taken.Contains(i)).ToList();
        var windows = WindowQuery.Candidates()
            .Where(w => w.Monitor == monitor.Handle && !w.IsMinimised && !c.Slots.ContainsKey(w.Handle))
            .ToList();

        var placed = 0;
        foreach (var (w, zone) in windows.Zip(free))
        {
            var hwnd = (IntPtr)w.Handle;
            var previous = RemoveSlot(hwnd);
            var before = previous is not null ? previous.Restore
                : w.IsMaximised ? null : WindowQuery.WindowRect(hwnd);
            Slot(c, hwnd, [zone], before);
            WindowPlacer.Fit(hwnd, zones[zone]);
            Slotted?.Invoke(hwnd, monitor);
            placed++;
        }

        if (c.Slots.Count == 0) _containers.Remove(c);
        Save();
        SyncHandles();

        Notice?.Invoke(placed switch
        {
            0 when free.Count == 0 => "Every zone is already taken.",
            0 => $"No other open windows on {monitor.ShortName} to fill the zones with.",
            1 => "Put 1 window into the layout.",
            _ => $"Put {placed} windows into the layout.",
        });
    }

    // ---- Handles ----

    private void SyncHandles()
    {
        Prune();
        var monitors = Monitors.All();

        foreach (var c in _containers.ToList())
        {
            var dragging = _drag?.Container == c;
            var onScreen = c.Slots.Keys.Any(h => WindowQuery.IsCandidate((IntPtr)h) && !Win32.IsIconic((IntPtr)h));
            var monitor = monitors.FirstOrDefault(m => m.DeviceName == c.DeviceName);

            if (dragging || (onScreen && monitor is not null))
                ShowHandles(c, monitor);
            else
                HideHandles(c);
        }

        foreach (var orphan in _handles.Keys.Where(k => !_containers.Contains(k)).ToList())
            HideHandles(orphan);
    }

    private void ShowHandles(ZoneContainer c, MonitorInfo? monitor)
    {
        var isNew = !_handles.TryGetValue(c, out var h);
        if (h is null)
        {
            h = new Handles(new ContainerHandleWindow(ContainerHandleWindow.Kind.Chip),
                            new ContainerHandleWindow(ContainerHandleWindow.Kind.Resize));
            Wire(c, h.Chip);
            Wire(c, h.Grip);
            _handles[c] = h;
        }

        h.Chip.SetLabel($"{c.Layout.Name} · {c.Slots.Count}");
        h.Chip.SetCurrentLayout(c.LayoutId);
        PositionHandles(c, h, monitor);

        if (isNew)
        {
            h.Chip.Show();
            h.Grip.Show();
            // Born on top of everything; tuck them in just above the layout instead.
            PlaceHandlesInZOrder(c, h);
        }
    }

    private void PositionHandles(ZoneContainer c, Handles h, MonitorInfo? monitor)
    {
        var scale = monitor?.Scale ?? 1.0;
        var f = c.Frame;

        // The tab hangs from the frame's top edge, centred; the expanded panel opens downward beneath it.
        var chip = h.Chip.PixelSizeAt(scale);
        h.Chip.Position = new PixelPoint(f.X + (f.Width - chip.Width) / 2, f.Y);

        var grip = h.Grip.PixelSizeAt(scale);
        h.Grip.Position = new PixelPoint(f.Right - grip.Width, f.Bottom - grip.Height);
    }

    /// <summary>Lift the handles above the layout: to the top if a layout window is in front, else just
    /// beneath whatever app is.</summary>
    private void PlaceHandlesInZOrder(ZoneContainer c, Handles h)
    {
        var fg = Win32.GetForegroundWindow();
        var insertAfter = c.Slots.ContainsKey(fg) || fg == IntPtr.Zero ? IntPtr.Zero /* HWND_TOP */ : fg;
        foreach (var w in new[] { h.Chip, h.Grip })
        {
            var hwnd = w.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd != IntPtr.Zero && hwnd != insertAfter)
                Win32.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, RaiseFlags);
        }
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        foreach (var (c, h) in _handles)
        {
            h.Chip.Collapse();
            if (c.Slots.ContainsKey(hwnd)) PlaceHandlesInZOrder(c, h);
        }
    }

    private void HideHandles(ZoneContainer c)
    {
        if (!_handles.Remove(c, out var h)) return;
        h.Chip.Close();
        h.Grip.Close();
    }

    private void Wire(ZoneContainer c, ContainerHandleWindow handle)
    {
        handle.DragStarted += () => BeginDrag(c, handle.HandleKind);
        handle.Dragged += (dx, dy) => DragTo(c, dx, dy);
        handle.DragEnded += () => EndDrag(c);
        handle.DoubleClicked += () => FillMonitor(c);
        handle.ExpandedChanged += () =>
        {
            if (!_handles.TryGetValue(c, out var h)) return;
            PositionHandles(c, h, MonitorOf(c));
            // Collapsed again: drop from topmost back to just above the layout.
            if (!h.Chip.IsExpanded) PlaceHandlesInZOrder(c, h);
        };
        handle.LayoutChosen += id =>
        {
            if (MonitorOf(c) is { } m) LayoutPicked?.Invoke(m, id);
        };
        handle.ActionChosen += action => OnChipAction(c, action);
    }

    private void OnChipAction(ZoneContainer c, ChipAction action)
    {
        switch (action)
        {
            case ChipAction.FillWithOpenWindows:
                if (MonitorOf(c) is { } m) FillWithOpenWindows(m);
                break;
            case ChipAction.FillScreen:
                FillMonitor(c);
                break;
            case ChipAction.MinimiseLayout:
                MinimiseRequested?.Invoke(c.DeviceName);
                break;
            case ChipAction.ReleaseWindows:
                Release(c);
                break;
        }
    }

    /// <summary>Let every window go — each back to the size and place it had before it was snapped — and
    /// drop the container.</summary>
    private void Release(ZoneContainer c)
    {
        var slots = c.Slots.Values.ToList();
        _containers.Remove(c);
        HideHandles(c);

        var restores = slots
            .Where(s => s.Restore is not null)
            .Select(s => ((IntPtr)s.Handle, s.Restore!.Value))
            .ToList();
        WindowPlacer.PlaceTogether(restores);

        foreach (var s in slots) Unslotted?.Invoke((IntPtr)s.Handle);
        Save();
    }

    // ---- Moving / resizing ----

    private void BeginDrag(ZoneContainer c, ContainerHandleWindow.Kind kind)
    {
        Prune();
        // Margins are measured once up front — they don't change mid-drag, and re-reading every frame
        // would cost a cross-process query per window per tick.
        var margins = c.Slots.Keys.ToDictionary(h => h, h => WindowPlacer.MarginsOf((IntPtr)h));
        _drag = new DragState(c, kind, margins);
        _apply.Start();
    }

    private void DragTo(ZoneContainer c, int dx, int dy)
    {
        if (_drag?.Container != c) return;

        if (_drag.Kind == ContainerHandleWindow.Kind.Chip)
        {
            c.Frame = ZoneContainer.Offset(_drag.StartFrame, dx, dy);
        }
        else
        {
            var work = MonitorOf(c)?.WorkArea ?? _drag.StartFrame;
            c.Frame = ZoneContainer.ResizeFromCorner(_drag.StartFrame, dx, dy, work);
        }
        _drag.Pending = true;
    }

    private void ApplyPendingDrag()
    {
        if (_drag is not { Pending: true } d) return;
        d.Pending = false;

        var zones = d.Container.Zones(_settings().ZoneSpacing);
        var moves = d.Container.Slots.Values
            .Select(s => ((IntPtr)s.Handle,
                d.Margins.GetValueOrDefault(s.Handle).Grow(ZoneContainer.TargetFor(s, zones))))
            .ToList();
        WindowPlacer.PlaceTogether(moves);

        if (_handles.TryGetValue(d.Container, out var h))
            PositionHandles(d.Container, h, MonitorAt(CentreOf(d.Container.Frame)));
    }

    private void EndDrag(ZoneContainer c)
    {
        if (_drag?.Container != c) return;
        var d = _drag;
        ApplyPendingDrag();
        _apply.Stop();
        _drag = null;

        if (d.Kind == ContainerHandleWindow.Kind.Chip)
            Land(c, d);

        // Fit with fresh margins: a window that changed monitor may also have changed DPI (and border size).
        Reflow(c);
        DispatcherTimer.RunOnce(() => Reflow(c), TimeSpan.FromMilliseconds(150));
        Save();
        SyncHandles();
    }

    /// <summary>After a move: settle the frame onto whichever monitor its centre landed on.</summary>
    private void Land(ZoneContainer c, DragState d)
    {
        var target = MonitorAt(CentreOf(c.Frame));
        if (target is null)
        {
            c.Frame = d.StartFrame;
            return;
        }

        if (target.DeviceName != d.StartDevice)
        {
            if (ContainerOn(target.DeviceName) is { } other && other != c)
            {
                Notice?.Invoke($"{target.ShortName} already has a zone layout — moved back.");
                c.Frame = d.StartFrame;
                return;
            }
            c.DeviceName = target.DeviceName;
            Rehomed?.Invoke(d.StartDevice, target);
        }

        c.Frame = ZoneContainer.ClampInto(c.Frame, target.WorkArea);
    }

    private void FillMonitor(ZoneContainer c)
    {
        if (MonitorOf(c) is not { } monitor) return;
        if (_handles.TryGetValue(c, out var h)) h.Chip.Collapse();
        c.Frame = monitor.WorkArea;
        Reflow(c);
        Save();
        SyncHandles();
    }

    /// <summary>Put every slotted window back into its zones (fresh margins).</summary>
    private void Reflow(ZoneContainer c)
    {
        var zones = c.Zones(_settings().ZoneSpacing);
        foreach (var s in c.Slots.Values)
            WindowPlacer.Fit((IntPtr)s.Handle, ZoneContainer.TargetFor(s, zones));
        if (_handles.TryGetValue(c, out var h))
            PositionHandles(c, h, MonitorOf(c));
    }

    // ---- Helpers ----

    private ZoneContainer GetOrCreate(MonitorInfo monitor)
    {
        if (ContainerOn(monitor.DeviceName) is { } c) return c;
        c = new ZoneContainer(monitor.DeviceName, LayoutIdFor(monitor), monitor.WorkArea);
        _containers.Add(c);
        return c;
    }

    private static void Slot(ZoneContainer c, IntPtr hwnd, IEnumerable<int> zones, PixelRect? restore)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        c.Slots[hwnd] = new ZoneSlot(hwnd, pid, [.. zones], restore);
    }

    /// <summary>Take the window out of whichever layout holds it; returns its old slot, if any.</summary>
    private ZoneSlot? RemoveSlot(IntPtr hwnd)
    {
        foreach (var c in _containers)
        {
            if (c.Slots.Remove(hwnd, out var slot)) return slot;
        }
        return null;
    }

    private void Prune()
    {
        var changed = false;
        foreach (var c in _containers)
        {
            foreach (var dead in c.Slots.Values.Where(s => !IsAlive(s)).ToList())
                changed |= c.Slots.Remove(dead.Handle);
        }
        if (changed) Save();
    }

    private static bool IsAlive(ZoneSlot s)
    {
        if (!Win32.IsWindow((IntPtr)s.Handle)) return false;
        Win32.GetWindowThreadProcessId((IntPtr)s.Handle, out var pid);
        return pid == s.ProcessId;
    }

    private static MonitorInfo? MonitorOf(ZoneContainer c) => Monitors.All().FirstOrDefault(m => m.DeviceName == c.DeviceName);

    private static (int X, int Y) CentreOf(PixelRect r) => (r.X + r.Width / 2, r.Y + r.Height / 2);

    private static MonitorInfo? MonitorAt((int X, int Y) p) => Monitors.All().FirstOrDefault(m => m.Contains(p.X, p.Y));

    private void Save() => _file.Save(_containers.Select(ZoneContainerState.From).ToList());

    public void Dispose()
    {
        _sync.Stop();
        _apply.Stop();
        foreach (var c in _handles.Keys.ToList()) HideHandles(c);
    }
}
