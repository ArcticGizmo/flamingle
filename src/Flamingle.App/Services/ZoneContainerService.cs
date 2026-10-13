using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.App.Views;
using Flamingle.Core.Geometry;
using Flamingle.Core.Persistence;
using Flamingle.Core.Settings;
using Flamingle.Core.Zones;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Services;

/// <summary>
/// Owns the zone containers — one per monitor, each a layout laid out in a movable frame, plus the windows
/// snapped into its zones — and their handles: a pull-tab on the top edge (drag to move, click for presets
/// and actions) and a strip over each divider between zones (drag to re-split). Moving the frame or a
/// divider re-places every slotted window in one <c>DeferWindowPos</c> batch, so they move as a unit.
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

    // Layouts whose divider strips are showing ("Adjust splits").
    private readonly HashSet<ZoneContainer> _adjusting = [];

    // A zoned window being resized by its own edge (linked resize).
    private LinkedResize? _resize;

    private sealed class LinkedResize(ZoneContainer container, ZoneSlot slot, Dictionary<long, WindowPlacer.Margins> margins)
    {
        public ZoneContainer Container { get; } = container;
        public ZoneSlot Slot { get; } = slot;
        public IReadOnlyList<ZoneFraction> StartZones { get; } = container.Layout.Zones;
        public Dictionary<long, WindowPlacer.Margins> Margins { get; } = margins;
    }
    private readonly DispatcherTimer _sync;
    private readonly DispatcherTimer _apply;

    private DragState? _drag;

    // The smallest a zone can be squeezed to by dragging a divider (physical px).
    private const int MinZoneWidth = 240;
    private const int MinZoneHeight = 160;

    // Divider strips are at least this thick (DIP), even when the zones have no spacing between them.
    private const double MinDividerThickness = 8;

    // Hover-to-reveal: the cursor lingering this close to a tab (DIP) for this long lifts the tab above
    // everything; it drops back once the cursor has been away for the grace period.
    private const double HoverReach = 36;
    private static readonly TimeSpan HoverDwell = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(700);

    private sealed class Handles(ContainerHandleWindow chip)
    {
        public ContainerHandleWindow Chip { get; } = chip;
        public List<DividerHandleWindow> Dividers { get; } = [];
        public IEnumerable<Window> All => [Chip, .. Dividers];

        public double Scale { get; set; } = 1.0;
        public DateTime? NearSince { get; set; }
        public DateTime? AwaySince { get; set; }
    }

    private enum DragKind { Move, Divider }

    private sealed class DragState(ZoneContainer container, DragKind kind, ZoneDivider? divider, Dictionary<long, WindowPlacer.Margins> margins)
    {
        public ZoneContainer Container { get; } = container;
        public DragKind Kind { get; } = kind;
        public ZoneDivider? Divider { get; } = divider;
        public PixelRect StartFrame { get; } = container.Frame;
        public IReadOnlyList<ZoneFraction> StartZones { get; } = container.Layout.Zones;
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

        _hover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _hover.Tick += (_, _) => HoverTick();
    }

    private readonly DispatcherTimer _hover;

    /// <summary>A window was put into a layout's zones (by drag, or "fill with open windows").</summary>
    public event Action<IntPtr, MonitorInfo>? Slotted;

    /// <summary>A window left its layout (dragged out, or released).</summary>
    public event Action<IntPtr>? Unslotted;

    /// <summary>A container moved to another monitor (from device, to monitor) — its zone flock follows.</summary>
    public event Action<string, MonitorInfo>? Rehomed;

    /// <summary>The chip's "Minimise layout" (device name of the layout's monitor).</summary>
    public event Action<string>? MinimiseRequested;

    /// <summary>A layout preset was picked from a chip (monitor, layout id).</summary>
    public event Action<MonitorInfo, string>? LayoutPicked;

    public event Action<string>? Notice;

    /// <summary>The gaps toggle in a layout tab was flipped (the setting is global — the app owns it).</summary>
    public event Action<bool>? GapsToggled;

    /// <summary>Re-fit every layout's windows — after a setting that changes the zones (e.g. gaps).</summary>
    public void ReflowAll()
    {
        Prune();
        foreach (var c in _containers) Reflow(c);
        SyncHandles();
    }

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
        _hover.Start();
    }

    // ---- Queries used by the drag-to-snap overlay ----

    public ZoneContainer? ContainerOn(string deviceName) => _containers.FirstOrDefault(c => c.DeviceName == deviceName);

    public string LayoutIdFor(MonitorInfo monitor)
        => ContainerOn(monitor.DeviceName)?.LayoutId ?? _settings().LayoutIdFor(monitor.DeviceName);

    /// <summary>The zones for a monitor: its container's (in the container's frame) or the default layout
    /// filling the work area.</summary>
    public IReadOnlyList<PixelRect> ZonesFor(MonitorInfo monitor)
    {
        var spacing = _settings().EffectiveZoneSpacing;
        return ContainerOn(monitor.DeviceName)?.Zones(spacing)
            ?? ZoneMath.Resolve(ZoneLayout.ById(LayoutIdFor(monitor)), monitor.WorkArea, spacing);
    }

    // ---- Membership ----

    /// <summary>The slot holding a window, if it's in a layout.</summary>
    public ZoneSlot? SlotOf(IntPtr hwnd)
    {
        foreach (var c in _containers)
        {
            if (c.Slots.TryGetValue(hwnd, out var slot)) return slot;
        }
        return null;
    }

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

        // Already back to its old size (restored mid-drag)? Then there's nothing to do.
        if (slot.Restore is { } restore && !Win32.IsZoomed(hwnd) && WindowQuery.WindowRect(hwnd) is { } current
            && (current.Width != restore.Width || current.Height != restore.Height))
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
        var zones = c.Zones(_settings().EffectiveZoneSpacing);

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
            h = new Handles(new ContainerHandleWindow());
            WireChip(c, h.Chip);
            _handles[c] = h;
        }

        h.Chip.SetLabel($"{c.Layout.Name} · {c.Slots.Count}");
        h.Chip.SetCurrentLayout(c.LayoutId);
        h.Chip.SetGaps(_settings().ZoneGaps);
        h.Chip.SetAdjusting(_adjusting.Contains(c));
        SyncDividers(c, h);
        PositionHandles(c, h, monitor);

        if (isNew)
        {
            h.Chip.Show();
            // Born on top of everything; tuck it in just above the layout instead.
            PlaceHandlesInZOrder(c, h);
        }
    }

    /// <summary>One strip per divider while the user is adjusting splits (none otherwise). Rebuilt when the
    /// layout's dividers change (never mid-drag).</summary>
    private void SyncDividers(ZoneContainer c, Handles h)
    {
        if (_drag?.Container == c) return;

        if (!_adjusting.Contains(c))
        {
            CloseDividers(h);
            return;
        }

        var dividers = c.Dividers();
        var same = dividers.Count == h.Dividers.Count
            && dividers.Select(d => d.Vertical).SequenceEqual(h.Dividers.Select(w => w.Vertical));
        if (same) return;

        CloseDividers(h);
        for (var i = 0; i < dividers.Count; i++)
        {
            var w = new DividerHandleWindow(dividers[i].Vertical);
            var index = i;
            w.DragStarted += () => BeginDrag(c, DragKind.Divider, index);
            w.Dragged += (dx, dy) => DragTo(c, dx, dy);
            w.DragEnded += () => EndDrag(c);
            w.DoubleClicked += () => EvenOut(c);
            h.Dividers.Add(w);
        }
        PositionHandles(c, h, MonitorOf(c));
        foreach (var w in h.Dividers) w.Show();
    }

    private static void CloseDividers(Handles h)
    {
        foreach (var w in h.Dividers) w.Close();
        h.Dividers.Clear();
    }

    /// <summary>Show / hide a layout's divider strips (the tab's "Adjust splits").</summary>
    private void SetAdjusting(ZoneContainer c, bool on)
    {
        if (on ? !_adjusting.Add(c) : !_adjusting.Remove(c)) return;
        if (_handles.TryGetValue(c, out var h)) h.Chip.SetAdjusting(on);
        SyncHandles();
        if (on && c.Dividers().Count == 0)
            Notice?.Invoke($"{c.Layout.Name} has only one zone — nothing to split.");
    }

    /// <summary>
    /// The tab normally sits just above its layout, so the layout's own windows (or anything brought in
    /// front) can bury it. If the cursor lingers near a tab, lift it above everything; once the cursor has
    /// been away for a moment (and the panel's closed), let it drop back.
    /// </summary>
    private void HoverTick()
    {
        if (_handles.Count == 0 || !Win32.GetCursorPos(out var cursor)) return;
        var now = DateTime.UtcNow;

        foreach (var (c, h) in _handles.ToList())
        {
            var size = h.Chip.PixelSizeAt(h.Scale);
            var reach = (int)(HoverReach * h.Scale);
            var pos = h.Chip.Position;
            var near = cursor.x >= pos.X - reach && cursor.x < pos.X + size.Width + reach
                    && cursor.y >= pos.Y - reach && cursor.y < pos.Y + size.Height + reach;

            if (near)
            {
                h.AwaySince = null;
                h.NearSince ??= now;
                if (!h.Chip.IsPeeking && now - h.NearSince >= HoverDwell)
                    h.Chip.SetPeek(true);
            }
            else
            {
                h.NearSince = null;
                if (!h.Chip.IsPeeking || h.Chip.IsExpanded || _drag?.Container == c) continue;
                h.AwaySince ??= now;
                if (now - h.AwaySince < HoverGrace) continue;

                h.AwaySince = null;
                h.Chip.SetPeek(false);
                PlaceHandlesInZOrder(c, h);
            }
        }
    }

    private void PositionHandles(ZoneContainer c, Handles h, MonitorInfo? monitor)
    {
        var scale = monitor?.Scale ?? 1.0;
        h.Scale = scale;
        var f = c.Frame;

        // The tab hangs from the frame's top edge, centred; the expanded panel opens downward beneath it.
        var dividers = c.Dividers();
        var zones = c.Zones(_settings().EffectiveZoneSpacing);
        // Place the (collapsed) tab first, then centre the window on it — so opening the panel doesn't make the
        // tab jump.
        var tabWidth = (int)Math.Ceiling(ContainerHandleWindow.TabWidth * scale);
        var tabX = AvoidCaptionButtons(f.X + (f.Width - tabWidth) / 2, tabWidth, dividers, zones, scale);
        var chip = h.Chip.PixelSizeAt(scale);
        h.Chip.Position = new PixelPoint(tabX + tabWidth / 2 - chip.Width / 2, f.Y);

        // Each divider strip covers the gap between its zones.
        if (dividers.Count != h.Dividers.Count) return;
        var thickness = (int)Math.Ceiling(MinDividerThickness * scale);
        for (var i = 0; i < dividers.Count; i++)
            h.Dividers[i].Place(ZoneDividers.HandleRect(dividers[i], zones, thickness), scale);
    }

    // Roughly the width of a window's minimise / maximise / close buttons (DIP), and the nudge past a split.
    private const double CaptionButtonsWidth = 140;
    private const double TabNudge = 12;

    /// <summary>
    /// A vertical split that reaches the top puts the left window's close button right next to the centre.
    /// If the tab would sit over a window's caption buttons (just left of such a split), move it to just right
    /// of the split instead — over the start of the next window's title bar.
    /// </summary>
    private static int AvoidCaptionButtons(int x, int width, IReadOnlyList<ZoneDivider> dividers, IReadOnlyList<PixelRect> zones, double scale)
    {
        var buttons = (int)(CaptionButtonsWidth * scale);
        foreach (var d in dividers.Where(d => d.Vertical && d.Start < 1e-6).OrderBy(d => d.Position))
        {
            var gap = ZoneDividers.HandleRect(d, zones, minThickness: 0);
            if (x < gap.Right && x + width > gap.X - buttons)
                return gap.Right + (int)(TabNudge * scale);
        }
        return x;
    }

    /// <summary>Lift the tab above the layout: to the top if a layout window is in front, else just beneath
    /// whatever app is. (Divider strips are topmost while shown, so they're left alone.)</summary>
    private void PlaceHandlesInZOrder(ZoneContainer c, Handles h)
    {
        if (h.Chip.IsExpanded || h.Chip.IsPeeking) return;   // topmost while open / hovered
        var fg = Win32.GetForegroundWindow();
        var insertAfter = c.Slots.ContainsKey(fg) || fg == IntPtr.Zero ? IntPtr.Zero /* HWND_TOP */ : fg;
        var hwnd = h.Chip.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero && hwnd != insertAfter)
            Win32.SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0, RaiseFlags);
    }

    private void OnForegroundChanged(IntPtr hwnd)
    {
        foreach (var (c, h) in _handles.ToList())
        {
            h.Chip.Collapse();
            if (c.Slots.ContainsKey(hwnd)) PlaceHandlesInZOrder(c, h);
            // Switching to some other app ends split-adjusting (the strips are topmost — don't leave them).
            else if (_adjusting.Contains(c)) SetAdjusting(c, false);
        }
    }

    private void HideHandles(ZoneContainer c)
    {
        _adjusting.Remove(c);
        if (!_handles.Remove(c, out var h)) return;
        foreach (var w in h.All) w.Close();
    }

    private void WireChip(ZoneContainer c, ContainerHandleWindow handle)
    {
        handle.DragStarted += () => BeginDrag(c, DragKind.Move, -1);
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
        handle.GapsToggled += on => GapsToggled?.Invoke(on);
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
            case ChipAction.EvenOutSplits:
                EvenOut(c);
                break;
            case ChipAction.AdjustSplits:
                SetAdjusting(c, !_adjusting.Contains(c));
                break;
        }
    }

    // ---- Linked resize ----

    /// <summary>
    /// A zoned window is being resized by its own edge (called on every tick of Windows' resize loop). Each
    /// inner edge that has moved drags its divider along, and the windows on the other side follow live —
    /// like Windows 11 Snap Groups. Outer edges have nothing on the other side; they snap back on release.
    /// </summary>
    public void ResizeTick(IntPtr hwnd)
    {
        if (!_settings().LinkedResize || _drag is not null) return;

        if (_resize is null)
        {
            var owner = _containers.FirstOrDefault(x => x.Slots.ContainsKey(hwnd));
            if (owner is null) return;
            var margins = owner.Slots.Keys
                .Where(h => h != (long)hwnd)
                .ToDictionary(h => h, h => WindowPlacer.MarginsOf((IntPtr)h));
            _resize = new LinkedResize(owner, owner.Slots[hwnd], margins);
        }

        var r = _resize;
        if (r.Slot.Handle != (long)hwnd) return;

        var c = r.Container;
        var f = c.Frame;
        var spacing = _settings().EffectiveZoneSpacing;
        var target = ZoneContainer.TargetFor(r.Slot, ZoneMath.Resolve(new ZoneLayout("", "", r.StartZones), f, spacing));
        var visible = WindowQuery.Snapshot(hwnd).Bounds;

        var zones = r.StartZones;
        void Follow(ZoneDividers.Edge edge, int now, int was)
        {
            if (Math.Abs(now - was) <= 2) return;
            if (ZoneDividers.ForEdge(r.StartZones, r.Slot.Zones, edge) is not { } d) return;

            // The divider sits half a gap beyond the window's visible edge.
            var trailing = edge is ZoneDividers.Edge.Right or ZoneDividers.Edge.Bottom;
            var line = trailing ? now + spacing / 2.0 : now - spacing / 2.0;
            var position = d.Vertical ? (line - f.X) / f.Width : (line - f.Y) / f.Height;
            var min = d.Vertical ? MinZoneWidth / (double)f.Width : MinZoneHeight / (double)f.Height;
            zones = ZoneDividers.Move(zones, d, position, min);
        }

        Follow(ZoneDividers.Edge.Left, visible.X, target.X);
        Follow(ZoneDividers.Edge.Right, visible.Right, target.Right);
        Follow(ZoneDividers.Edge.Top, visible.Y, target.Y);
        Follow(ZoneDividers.Edge.Bottom, visible.Bottom, target.Bottom);

        c.CustomZones = zones;
        var resolved = c.Zones(spacing);
        var moves = c.Slots.Values
            .Where(s => s.Handle != (long)hwnd)
            .Select(s => ((IntPtr)s.Handle, r.Margins.GetValueOrDefault(s.Handle).Grow(ZoneContainer.TargetFor(s, resolved))))
            .ToList();
        WindowPlacer.PlaceTogether(moves);
    }

    /// <summary>The resize ended: settle every window (the resized one included) exactly into its zones.</summary>
    public void ResizeEnded(IntPtr hwnd)
    {
        if (_resize is not { } r || r.Slot.Handle != (long)hwnd) return;
        _resize = null;
        Reflow(r.Container);
        Save();
        SyncHandles();
    }

    /// <summary>Back to the preset's own proportions.</summary>
    private void EvenOut(ZoneContainer c)
    {
        if (c.CustomZones is null) return;
        c.CustomZones = null;
        Reflow(c);
        Save();
        SyncHandles();
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

    private void BeginDrag(ZoneContainer c, DragKind kind, int dividerIndex)
    {
        Prune();
        var dividers = c.Dividers();
        var divider = kind == DragKind.Divider && dividerIndex >= 0 && dividerIndex < dividers.Count
            ? dividers[dividerIndex]
            : null;
        if (kind == DragKind.Divider && divider is null) return;

        // Margins are measured once up front — they don't change mid-drag, and re-reading every frame
        // would cost a cross-process query per window per tick.
        var margins = c.Slots.Keys.ToDictionary(h => h, h => WindowPlacer.MarginsOf((IntPtr)h));
        _drag = new DragState(c, kind, divider, margins);
        _apply.Start();
    }

    private void DragTo(ZoneContainer c, int dx, int dy)
    {
        if (_drag?.Container != c) return;

        if (_drag.Kind == DragKind.Move)
        {
            c.Frame = ZoneContainer.Offset(_drag.StartFrame, dx, dy);
        }
        else if (_drag.Divider is { } d)
        {
            // Pixels → fraction of the frame; never squeeze a zone below a usable size.
            var f = c.Frame;
            var position = d.Vertical ? d.Position + dx / (double)f.Width : d.Position + dy / (double)f.Height;
            var min = d.Vertical ? MinZoneWidth / (double)f.Width : MinZoneHeight / (double)f.Height;
            c.CustomZones = ZoneDividers.Move(_drag.StartZones, d, position, min);
        }
        _drag.Pending = true;
    }

    private void ApplyPendingDrag()
    {
        if (_drag is not { Pending: true } d) return;
        d.Pending = false;

        var zones = d.Container.Zones(_settings().EffectiveZoneSpacing);
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

        if (d.Kind == DragKind.Move)
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
        var zones = c.Zones(_settings().EffectiveZoneSpacing);
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
        _hover.Stop();
        foreach (var c in _handles.Keys.ToList()) HideHandles(c);
    }
}
