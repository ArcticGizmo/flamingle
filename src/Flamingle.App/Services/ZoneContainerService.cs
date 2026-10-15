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
/// snapped into its zones. Layouts show nothing on screen until edit mode (the edit-layout hotkey): then the
/// layout gets an outline you drag to move it, a panel of presets and actions, and a strip over each divider
/// between zones (drag to re-split). Moving the frame or a divider re-places every slotted window in one
/// <c>DeferWindowPos</c> batch, so they move as a unit.
/// </summary>
internal sealed class ZoneContainerService : IDisposable
{
    private readonly Func<AppSettings> _settings;
    private readonly WinEventHooks _hooks;
    private readonly JsonFile<List<ZoneContainerState>> _file;
    private readonly List<ZoneContainer> _containers = [];
    private readonly Dictionary<ZoneContainer, Handles> _handles = [];

    // Each monitor's work area as last seen, to notice app bars coming and going.
    private readonly Dictionary<string, PixelRect> _workAreas = [];

    // Receives Windows' work-area / display-change broadcasts.
    private MessageWindow? _broadcasts;

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

    // The panel sits this far (DIP) below the top of the layout it edits.
    private const double PanelOffset = 16;

    private sealed class Handles(LayoutPanelWindow panel)
    {
        public LayoutPanelWindow Panel { get; } = panel;
        public List<DividerHandleWindow> Dividers { get; } = [];
        public IEnumerable<Window> All => [Panel, .. Dividers];
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

        // One-shot: a burst of window events (a desktop switch cloaks a dozen windows) becomes one sync, run once
        // the windows have settled (a minimise has to land before the window reads as minimised).
        _sync = new DispatcherTimer { Interval = SyncDelay };
        _sync.Tick += (_, _) =>
        {
            _sync.Stop();
            SyncHandles();
        };

        // Window moves during a handle drag are coalesced to one batch per frame.
        _apply = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
        _apply.Tick += (_, _) => ApplyPendingDrag();
    }

    /// <summary>A window was put into a layout's zones (by drag, or "fill with open windows").</summary>
    public event Action<IntPtr, MonitorInfo>? Slotted;

    /// <summary>A window left its layout (dragged out, or released).</summary>
    public event Action<IntPtr>? Unslotted;

    /// <summary>A container moved to another monitor (from device, to monitor) — its zone flock follows.</summary>
    public event Action<string, MonitorInfo>? Rehomed;

    /// <summary>Two layouts swapped monitors — their zone flocks swap too.</summary>
    public event Action<MonitorInfo, MonitorInfo>? Swapped;

    /// <summary>The chip's "Minimise layout" (device name of the layout's monitor).</summary>
    public event Action<string>? MinimiseRequested;

    /// <summary>A layout preset was picked from a panel or the snap strip (monitor, layout id).</summary>
    public event Action<MonitorInfo, string>? LayoutPicked;

    public event Action<string>? Notice;

    /// <summary>The gaps toggle in a layout's panel was flipped (the setting is global — the app owns it).</summary>
    public event Action<bool>? GapsToggled;

    /// <summary>What a layout's panel shows in its Flock section, by monitor device name. Flocks are the app's
    /// business — it supplies them.</summary>
    public Func<string, PanelFlocks>? PanelFlocksFor { get; set; }

    /// <summary>A panel renamed its layout's flock (device name, new name).</summary>
    public event Action<string, string>? FlockRenameRequested;

    /// <summary>A panel saved its layout's flock (device name, name to save it under).</summary>
    public event Action<string, string>? FlockSaveRequested;

    /// <summary>A panel deleted a saved flock.</summary>
    public event Action<Guid>? SavedFlockDeleteRequested;

    /// <summary>Flocks or saved flocks changed: bring any open panel up to date.</summary>
    public void RefreshPanels()
    {
        foreach (var (c, h) in _handles) SetPanelFlocks(c, h.Panel);
    }

    private void SetPanelFlocks(ZoneContainer c, LayoutPanelWindow panel)
    {
        if (PanelFlocksFor is { } flocks) panel.SetFlocks(flocks(c.DeviceName));
    }

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
            // A side panel may have docked or closed since the last run.
            c.Frame = state.WorkArea is { } then
                ? ZoneContainer.Refit(c.Frame, then, monitor.WorkArea)
                : ZoneContainer.ClampInto(c.Frame, monitor.WorkArea);
            _containers.Add(c);
        }
        foreach (var m in monitors) _workAreas[m.DeviceName] = m.WorkArea;
        Save();

        _hooks.ForegroundChanged += OnForegroundChanged;

        // A layout in edit mode needs windows on screen: re-check when one of its windows is minimised, restored,
        // hidden, shown, closed or moved to / from another virtual desktop — or a monitor comes or goes.
        // (Flamingle's own changes sync directly.)
        _hooks.MinimiseStarted += SyncIfSlotted;
        _hooks.MinimiseEnded += SyncIfSlotted;
        _hooks.Destroyed += SyncIfSlotted;
        _hooks.VisibilityChanged += SyncIfSlotted;
        _hooks.CloakChanged += SyncIfSlotted;

        _broadcasts = new MessageWindow("FlamingleWorkAreaWindow", receivesBroadcasts: true);
        _broadcasts.SettingChanged += OnSettingChanged;
        _broadcasts.DisplayChanged += () =>
        {
            Dispatcher.UIThread.Post(FollowWorkAreas);
            ScheduleSync();
        };
        _broadcasts.HotkeyPressed += id =>
        {
            switch (id)
            {
                case EscHotkeyId: ExitEdit(); break;
                case UndoHotkeyId: Undo(); break;
                case RedoHotkeyId or RedoAltHotkeyId: Redo(); break;
            }
        };
        SyncHandles();
    }

    private static readonly TimeSpan SyncDelay = TimeSpan.FromMilliseconds(60);

    private void SyncIfSlotted(IntPtr hwnd)
    {
        if (SlotOf(hwnd) is not null) ScheduleSync();
    }

    private void ScheduleSync()
    {
        _sync.Stop();
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
        var spacing = _settings().EffectiveZoneSpacing;
        return ContainerOn(monitor.DeviceName)?.Zones(spacing)
            ?? ZoneMath.Resolve(ZoneLayout.ById(LayoutIdFor(monitor)), monitor.WorkArea, spacing);
    }

    /// <summary>The zones <paramref name="layoutId"/> would have on a monitor — in its layout's frame if it has one
    /// (with its custom splits, if that's the current layout), else filling the work area. For the snap strip.</summary>
    public IReadOnlyList<PixelRect> ZonesFor(MonitorInfo monitor, string layoutId)
    {
        if (layoutId == LayoutIdFor(monitor)) return ZonesFor(monitor);
        var frame = ContainerOn(monitor.DeviceName)?.Frame ?? monitor.WorkArea;
        return ZoneMath.Resolve(ZoneLayout.ById(layoutId), frame, _settings().EffectiveZoneSpacing);
    }

    /// <summary>Switch a monitor's layout the way a layout tab does (the app saves the choice and re-flows) — for a
    /// window dropped on another layout in the snap strip.</summary>
    public void PickLayout(MonitorInfo monitor, string layoutId) => LayoutPicked?.Invoke(monitor, layoutId);

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

    /// <summary>The layout a window is snapped into, if any.</summary>
    public ZoneContainer? ContainerOf(IntPtr hwnd) => _containers.FirstOrDefault(c => c.Slots.ContainsKey(hwnd));

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
        BeginChange("change layout");
        c.LayoutId = layoutId;
        Reflow(c);
        EndChange();
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
            var monitor = monitors.FirstOrDefault(m => m.DeviceName == c.DeviceName);

            // An edited layout whose monitor has gone leaves edit mode. (One with no windows — none yet, or all
            // minimised or closed — stays: it can be set up empty.)
            if (_editing == c && !dragging && monitor is null) ExitEdit();

            if (_editing == c || dragging)
                ShowHandles(c, monitor);
            else
                HideHandles(c);
        }

        foreach (var orphan in _handles.Keys.Where(k => !_containers.Contains(k)).ToList())
            HideHandles(orphan);
    }

    /// <summary>
    /// A monitor's work area changed — an app bar came or went (a docked side panel like Perch's opening or
    /// closing, the taskbar resizing). Layouts there follow it: edges that were flush with the old work area move
    /// to the new one, and the windows re-flow, so the space a closed panel leaves behind gets used. Driven by
    /// Windows' <c>SPI_SETWORKAREA</c> / display-change broadcasts, not polled.
    /// </summary>
    private void FollowWorkAreas()
    {
        var changed = false;
        foreach (var monitor in Monitors.All())
        {
            var now = monitor.WorkArea;
            if (!_workAreas.TryGetValue(monitor.DeviceName, out var was))
            {
                _workAreas[monitor.DeviceName] = now;
                continue;
            }
            if (was == now) continue;
            if (ContainerOn(monitor.DeviceName) is { } busy && (_drag?.Container == busy || _resize?.Container == busy))
                continue;   // mid-drag: caught up when the drag ends

            _workAreas[monitor.DeviceName] = now;
            if (ContainerOn(monitor.DeviceName) is not { } c) continue;
            c.Frame = ZoneContainer.Refit(c.Frame, was, now);
            Reflow(c);
            changed = true;
        }

        if (!changed) return;
        Save();
        SyncHandles();
    }

    private const uint SPI_SETWORKAREA = 0x002F;

    private void OnSettingChanged(uint what)
    {
        // Act once the broadcast has been delivered (it's a sent message — don't move windows inside it).
        if (what == SPI_SETWORKAREA) Dispatcher.UIThread.Post(FollowWorkAreas);
    }

    private void ShowHandles(ZoneContainer c, MonitorInfo? monitor)
    {
        var isNew = !_handles.TryGetValue(c, out var h);
        if (h is null)
        {
            h = new Handles(new LayoutPanelWindow());
            WirePanel(c, h.Panel);
            _handles[c] = h;
        }

        h.Panel.SetLabel(c.Slots.Count == 0
            ? $"{c.Layout.Name} · no windows yet"
            : $"{c.Layout.Name} · {c.Slots.Count} window{(c.Slots.Count == 1 ? "" : "s")}");
        h.Panel.SetCurrentLayout(c.LayoutId);
        h.Panel.SetGaps(_settings().ZoneGaps);
        h.Panel.SetAdjusting(_adjusting.Contains(c));
        h.Panel.SetHistory(_history.CanUndo, _history.CanRedo);
        SetPanelFlocks(c, h.Panel);
        SyncDividers(c, h);
        PositionHandles(c, h, monitor);

        if (isNew) h.Panel.Show();
        Restack(h);
    }

    /// <summary>Edit mode's windows are all topmost; stack them frame, then split strips, then panel — so the strips
    /// and the panel can always be grabbed.</summary>
    private void Restack(Handles h)
    {
        const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;
        IEnumerable<Window> order = [.. _editFrame is null ? [] : new Window[] { _editFrame }, .. h.Dividers, h.Panel];
        foreach (var w in order)
        {
            if (w.TryGetPlatformHandle()?.Handle is { } hwnd && hwnd != IntPtr.Zero)
                Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, flags);
        }
    }

    /// <summary>Bring a layout's windows to the front (keeping their order among themselves) — after it lands on
    /// another monitor, where they'd otherwise keep their old place behind whatever was there.</summary>
    private static void BringToFront(ZoneContainer c)
    {
        const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER
                           | Win32.SWP_ASYNCWINDOWPOS;
        var order = Core.Grouping.ZOrder.Sort(c.Slots.Keys, WindowQuery.ZOrderTopFirst());
        var above = IntPtr.Zero;   // HWND_TOP for the first, then each one directly beneath the one before
        foreach (var h in order)
        {
            var hwnd = (IntPtr)h;
            if (Win32.IsIconic(hwnd) || Win32.IsHungAppWindow(hwnd)) continue;
            Win32.SetWindowPos(hwnd, above, 0, 0, 0, 0, flags);
            above = hwnd;
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

    /// <summary>Show / hide a layout's divider strips (the panel's "Adjust splits").</summary>
    private void SetAdjusting(ZoneContainer c, bool on, bool quiet = false)
    {
        if (on ? !_adjusting.Add(c) : !_adjusting.Remove(c)) return;
        if (_handles.TryGetValue(c, out var h)) h.Panel.SetAdjusting(on);
        SyncHandles();
        if (on && !quiet && c.Dividers().Count == 0)
            Notice?.Invoke($"{c.Layout.Name} has only one zone — nothing to split.");
    }

    // ---- Edit mode ----

    // The layout being edited (at most one), and the frame drawn over it.
    private ZoneContainer? _editing;
    private EditFrameWindow? _editFrame;
    private const int EscHotkeyId = 1;
    private const uint VK_ESCAPE = 0x1B;
    private const uint MOD_NOREPEAT = 0x4000;

    /// <summary>
    /// The edit-layout hotkey: leave edit mode if a layout is in it; otherwise put the layout on the monitor under the
    /// cursor into edit mode — an outline you can drag from anywhere to move the layout (onto another monitor too),
    /// its split strips, and its panel.
    /// </summary>
    public void ToggleEditAtCursor()
    {
        if (_editing is not null)
        {
            ExitEdit();
            return;
        }

        if (!Win32.GetCursorPos(out var cursor) || MonitorAt((cursor.x, cursor.y)) is not { } monitor) return;
        Prune();
        // Any monitor can be edited, windows or not — set a layout up (preset, frame, splits) before using it.
        EnterEdit(GetOrCreate(monitor), monitor);
    }

    private void EnterEdit(ZoneContainer c, MonitorInfo monitor)
    {
        _editing = c;

        var hotkey = Core.Hotkeys.Hotkey.TryParse(_settings().LayoutMenuHotkey)?.ToString();
        var frame = _editFrame = new EditFrameWindow(
            $"Drag to move the layout · double-click to fill the screen · Esc{(hotkey is null ? "" : $" or {hotkey}")} to finish");
        frame.DragStarted += () => BeginDrag(c, DragKind.Move, -1);
        frame.Dragged += (dx, dy) => DragTo(c, dx, dy);
        frame.DragEnded += () => EndDrag(c);
        frame.DoubleClicked += () => FillMonitor(c);
        frame.Place(c.Frame, monitor.Scale);
        frame.Show();   // first, so the split strips and the panel (also topmost) land above it

        RegisterEditKeys();
        _adjusting.Add(c);
        SyncHandles();   // shows the panel and split strips
    }

    /// <summary>Esc, Ctrl+Z and Ctrl+Y belong to edit mode while it's up (system-wide, since the panel never has
    /// focus) — except while a name is being typed in the panel.</summary>
    private void RegisterEditKeys()
    {
        _broadcasts?.RegisterHotkey(EscHotkeyId, MOD_NOREPEAT, VK_ESCAPE);
        _broadcasts?.RegisterHotkey(UndoHotkeyId, MOD_CONTROL | MOD_NOREPEAT, 'Z');
        _broadcasts?.RegisterHotkey(RedoHotkeyId, MOD_CONTROL | MOD_NOREPEAT, 'Y');
        _broadcasts?.RegisterHotkey(RedoAltHotkeyId, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 'Z');
    }

    private void UnregisterEditKeys()
    {
        foreach (var id in new[] { EscHotkeyId, UndoHotkeyId, RedoHotkeyId, RedoAltHotkeyId })
            _broadcasts?.UnregisterHotkey(id);
    }

    private void ExitEdit()
    {
        if (_editing is not { } c) return;
        _editing = null;
        UnregisterEditKeys();
        _editFrame?.Close();
        _editFrame = null;
        _adjusting.Remove(c);
        SyncHandles();
    }

    // ---- Undo / redo ----

    // Session-only history of layout position adjustments (moves, splits, layout switches) — every layout's
    // geometry before each change. Reached in edit mode: Ctrl+Z / Ctrl+Y, or the panel's buttons.
    private readonly LayoutHistory<ZoneContainer> _history = new();
    private Dictionary<ZoneContainer, LayoutGeometry>? _changeStart;
    private string _changeLabel = "";

    private const int UndoHotkeyId = 2;
    private const int RedoHotkeyId = 3;
    private const int RedoAltHotkeyId = 4;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;

    private Dictionary<ZoneContainer, LayoutGeometry> Geometry() => _containers.ToDictionary(c => c, LayoutGeometry.Of);

    /// <summary>Remember every layout's geometry before a change (a drag records once, at its end).</summary>
    private void BeginChange(string label)
    {
        _changeStart = Geometry();
        _changeLabel = label;
    }

    private void EndChange()
    {
        if (_changeStart is not { } before) return;
        _changeStart = null;
        _history.Record(_changeLabel, before, Geometry());
    }

    public void Undo() => ApplyHistoryStep(_history.Undo(CurrentGeometry), "Undid", "Nothing to undo.");

    public void Redo() => ApplyHistoryStep(_history.Redo(CurrentGeometry), "Redid", "Nothing to redo.");

    private LayoutGeometry? CurrentGeometry(ZoneContainer c) => _containers.Contains(c) ? LayoutGeometry.Of(c) : null;

    /// <summary>Put the layouts in a history step back where it says, re-flowing their windows; zone flocks follow
    /// any layout that changes monitor (a swap undone swaps back).</summary>
    private void ApplyHistoryStep(LayoutChange<ZoneContainer>? step, string verb, string nothing)
    {
        if (step is null)
        {
            Notice?.Invoke(nothing);
            return;
        }

        var monitors = Monitors.All();
        var moves = new List<(ZoneContainer C, string From)>();
        foreach (var (c, geometry) in step.Before)
        {
            if (!_containers.Contains(c)) continue;   // released since
            var from = c.DeviceName;
            geometry.ApplyTo(c);
            if (monitors.FirstOrDefault(m => m.DeviceName == c.DeviceName) is { } m)
                c.Frame = ZoneContainer.ClampInto(c.Frame, m.WorkArea);
            if (from != c.DeviceName) moves.Add((c, from));
        }

        MonitorInfo? On(string device) => monitors.FirstOrDefault(m => m.DeviceName == device);
        if (moves.Count == 2 && moves[0].From == moves[1].C.DeviceName && moves[1].From == moves[0].C.DeviceName
            && On(moves[0].From) is { } a && On(moves[1].From) is { } b)
        {
            Swapped?.Invoke(a, b);
        }
        else
        {
            foreach (var (c, from) in moves)
                if (On(c.DeviceName) is { } to) Rehomed?.Invoke(from, to);
        }

        foreach (var c in step.Before.Keys.Where(_containers.Contains))
        {
            Reflow(c);
            if (moves.Any(m => m.C == c)) BringToFront(c);
        }
        Save();
        SyncHandles();
        Notice?.Invoke($"{verb}: {step.Label}.");
    }

    private void PositionHandles(ZoneContainer c, Handles h, MonitorInfo? monitor)
    {
        var scale = monitor?.Scale ?? 1.0;
        var f = c.Frame;

        // The panel sits centred near the top of the layout.
        var panel = h.Panel.PixelSizeAt(scale);
        h.Panel.Position = new PixelPoint(f.X + (f.Width - panel.Width) / 2, f.Y + (int)(PanelOffset * scale));

        if (_editing == c && _editFrame is not null)
        {
            _editFrame.Place(f, scale);
            _editFrame.SetZones(c.Zones(_settings().EffectiveZoneSpacing), f);
        }

        // Each divider strip covers the gap between its zones.
        var dividers = c.Dividers();
        if (dividers.Count != h.Dividers.Count) return;
        var zones = c.Zones(_settings().EffectiveZoneSpacing);
        var thickness = (int)Math.Ceiling(MinDividerThickness * scale);
        for (var i = 0; i < dividers.Count; i++)
            h.Dividers[i].Place(ZoneDividers.HandleRect(dividers[i], zones, thickness), scale);
    }

    /// <summary>Edit mode can't be clicked through (the frame covers the layout), so any switch of app is the user
    /// moving on.</summary>
    private void OnForegroundChanged(IntPtr hwnd) => ExitEdit();

    private void HideHandles(ZoneContainer c)
    {
        if (_editing == c) ExitEdit();
        _adjusting.Remove(c);
        if (!_handles.Remove(c, out var h)) return;
        foreach (var w in h.All) w.Close();
    }

    private void WirePanel(ZoneContainer c, LayoutPanelWindow panel)
    {
        panel.LayoutChosen += id =>
        {
            if (MonitorOf(c) is { } m) LayoutPicked?.Invoke(m, id);
        };
        panel.ActionChosen += action => OnPanelAction(c, action);
        panel.GapsToggled += on => GapsToggled?.Invoke(on);
        panel.FlockRenamed += name => FlockRenameRequested?.Invoke(c.DeviceName, name);
        panel.FlockSaved += name => FlockSaveRequested?.Invoke(c.DeviceName, name);
        panel.SavedFlockDeleted += id => SavedFlockDeleteRequested?.Invoke(id);
        panel.TypingChanged += typing =>
        {
            if (_editing != c) return;
            if (typing) UnregisterEditKeys();
            else RegisterEditKeys();
        };
        // The Flock section grows and shrinks (saved flocks, the name field): keep the panel centred.
        panel.SizeChanged += (_, _) =>
        {
            if (_handles.TryGetValue(c, out var h) && ReferenceEquals(h.Panel, panel)) PositionHandles(c, h, MonitorOf(c));
        };
    }

    private void OnPanelAction(ZoneContainer c, PanelAction action)
    {
        // Split tweaks keep edit mode going; the other actions are done with it.
        if (action is not (PanelAction.AdjustSplits or PanelAction.EvenOutSplits or PanelAction.Undo or PanelAction.Redo))
            ExitEdit();

        switch (action)
        {
            case PanelAction.FillWithOpenWindows:
                if (MonitorOf(c) is { } m) FillWithOpenWindows(m);
                break;
            case PanelAction.FillScreen:
                FillMonitor(c);
                break;
            case PanelAction.MinimiseLayout:
                MinimiseRequested?.Invoke(c.DeviceName);
                break;
            case PanelAction.ReleaseWindows:
                Release(c);
                break;
            case PanelAction.EvenOutSplits:
                EvenOut(c);
                break;
            case PanelAction.AdjustSplits:
                SetAdjusting(c, !_adjusting.Contains(c));
                break;
            case PanelAction.Undo:
                Undo();
                break;
            case PanelAction.Redo:
                Redo();
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
            BeginChange("resize split");
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
        FollowWorkAreas();   // in case the work area changed mid-resize
        Reflow(r.Container);
        EndChange();
        Save();
        SyncHandles();
    }

    /// <summary>Back to the preset's own proportions.</summary>
    private void EvenOut(ZoneContainer c)
    {
        if (c.CustomZones is null) return;
        BeginChange("even out splits");
        c.CustomZones = null;
        Reflow(c);
        EndChange();
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
        BeginChange(kind == DragKind.Move ? "move layout" : "adjust split");

        // Picking a layout up brings its windows forward, so they don't pass behind other windows on the way.
        if (kind == DragKind.Move) BringToFront(c);
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
        FollowWorkAreas();   // in case the work area changed mid-drag

        if (d.Kind == DragKind.Move)
            Land(c, d);

        // Fit with fresh margins: a window that changed monitor may also have changed DPI (and border size).
        Reflow(c);
        DispatcherTimer.RunOnce(() => Reflow(c), TimeSpan.FromMilliseconds(150));
        EndChange();
        Save();
        SyncHandles();
    }

    /// <summary>
    /// After a move: settle the frame onto whichever monitor its centre landed on. Landing on another monitor carries
    /// the layout over at the same relative place and size (a full-screen layout fills the new screen); if that
    /// monitor already has a layout, the two swap.
    /// </summary>
    private void Land(ZoneContainer c, DragState d)
    {
        var target = MonitorAt(CentreOf(c.Frame));
        var source = Monitors.All().FirstOrDefault(m => m.DeviceName == d.StartDevice);
        if (target is null || source is null)
        {
            c.Frame = d.StartFrame;
            return;
        }

        if (target.DeviceName == d.StartDevice)
        {
            c.Frame = ZoneContainer.ClampInto(c.Frame, target.WorkArea);
            return;
        }

        c.Frame = ZoneContainer.MoveBetween(d.StartFrame, source.WorkArea, target.WorkArea);
        c.DeviceName = target.DeviceName;

        if (ContainerOn(target.DeviceName) is { } other && other != c)
        {
            other.Frame = ZoneContainer.MoveBetween(other.Frame, target.WorkArea, source.WorkArea);
            other.DeviceName = source.DeviceName;
            Reflow(other);
            BringToFront(other);
            Swapped?.Invoke(source, target);
            Notice?.Invoke($"Swapped the layouts on {source.ShortName} and {target.ShortName}.");
        }
        else
        {
            Rehomed?.Invoke(d.StartDevice, target);
        }
        BringToFront(c);
    }

    private void FillMonitor(ZoneContainer c)
    {
        if (MonitorOf(c) is not { } monitor) return;
        BeginChange("fill screen");
        c.Frame = monitor.WorkArea;
        Reflow(c);
        EndChange();
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

    private void Save() => _file.Save(_containers
        .Select(c => ZoneContainerState.From(c, _workAreas.TryGetValue(c.DeviceName, out var w) ? w : null))
        .ToList());

    public void Dispose()
    {
        _sync.Stop();
        _apply.Stop();
        ExitEdit();
        foreach (var c in _handles.Keys.ToList()) HideHandles(c);
        _broadcasts?.Dispose();
    }
}
