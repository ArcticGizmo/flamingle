using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.App.Views;
using Flamingle.App.Views.Design;
using Flamingle.App.Views.Settings;
using Flamingle.Core.Geometry;
using Flamingle.Core.Saved;
using Flamingle.Core.Zones;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Services;

/// <summary>
/// Design mode for a saved flock: every monitor is dimmed and shows the flock's layout as tiles — what starts in each
/// zone, or an empty zone waiting for something — and a floating panel edits whatever is selected. Click an empty zone
/// to choose what starts there (an open window, an installed app, a program, or a window dragged onto it); click a
/// window to change how it starts and where it goes; drag it onto another zone to move it; click a monitor's chip to
/// change its layout. Works on a copy (the editor's draft) until Save.
/// </summary>
internal sealed class FlockDesignService
{
    private readonly ZoneContainerService _zones;
    private readonly Action<SavedFlock> _save;
    private readonly Func<IReadOnlyList<WindowChoice>> _openWindows;
    private readonly Func<long, int, SavedWindow?> _capture;
    private readonly Action<SavedFlock, bool> _openForm;
    private readonly DispatcherTimer _dragInTimer;

    private SavedFlockEditor? _editor;
    private DesignPanelWindow? _panel;
    private bool _isNew;
    private IReadOnlyList<MonitorInfo> _monitors = [];
    private readonly Dictionary<string, DesignSurfaceWindow> _surfaces = [];

    // What each monitor shows now: its regions and its zones (physical px).
    private readonly Dictionary<string, (DesignScreen Screen, IReadOnlyList<PixelRect> Zones)> _screens = [];

    // Layouts picked for monitors the flock has no zoned windows on yet.
    private readonly Dictionary<string, string> _screenLayouts = [];

    private Selection _selection = new Selection.None();

    // "Drag a window onto a zone": the window being dragged, once the user picks one up.
    private bool _dragIn;
    private IntPtr _dragInWindow;
    private Selection _beforeDragIn = new Selection.None();

    private abstract record Selection
    {
        public sealed record None : Selection;
        public sealed record Window(int Index) : Selection;
        public sealed record Region(string Device, IReadOnlyList<int> Zones) : Selection;
        public sealed record Screen(string Device) : Selection;
    }

    public FlockDesignService(ZoneContainerService zones, WinEventHooks hooks, Action<SavedFlock> save,
        Func<IReadOnlyList<WindowChoice>> openWindows, Func<long, int, SavedWindow?> capture, Action<SavedFlock, bool> openForm)
    {
        _zones = zones;
        _save = save;
        _openWindows = openWindows;
        _capture = capture;
        _openForm = openForm;
        _dragInTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _dragInTimer.Tick += (_, _) => HighlightUnderCursor();
        hooks.MoveSizeStarted += OnMoveSizeStarted;
        hooks.MoveSizeEnded += OnMoveSizeEnded;
    }

    public bool IsOpen => _editor is not null;

    public event Action<string>? Notice;

    private SavedFlock Draft => _editor!.Draft;

    // ---- Opening and closing ----

    public void Open(SavedFlock flock, bool isNew)
    {
        if (IsOpen)
        {
            _panel?.Activate();
            Notice?.Invoke($"Finish designing \"{Draft.Name}\" first.");
            return;
        }

        _isNew = isNew;
        _monitors = Monitors.All();
        _screenLayouts.Clear();
        _selection = new Selection.None();

        var hooks = new DesignHooks(PlacementFor, Assign, BeginDragIn, ToForm);
        _editor = new SavedFlockEditor(flock, isNew, f =>
        {
            _isNew = false;
            _save(f);
        }, Close, _openWindows, _capture, hooks);
        _editor.DraftChanged += _ => Redraw();
        _editor.FocusChanged += OnEditorFocus;

        foreach (var m in _monitors)
        {
            var surface = new DesignSurfaceWindow(m.WorkArea, m.Scale);
            var device = m.DeviceName;
            surface.RegionClicked += region => OnRegionClicked(device, region);
            surface.WindowClicked += i => Select(new Selection.Window(i));
            surface.ScreenClicked += () => Select(new Selection.Screen(device));
            surface.BackgroundClicked += () => Select(new Selection.None());
            surface.WindowDragged += _ => HighlightUnderCursor();
            surface.WindowDropped += OnWindowDropped;
            _surfaces[device] = surface;
            surface.Show();
        }

        _panel = new DesignPanelWindow { Body = _editor };
        _panel.EscapePressed += Escape;
        _panel.SizeChanged += (_, _) => PositionPanel();
        _panel.Show();

        Redraw();
        PositionPanel();
        _panel.Activate();
    }

    private void Close()
    {
        EndDragIn();
        foreach (var s in _surfaces.Values) s.Close();
        _surfaces.Clear();
        _screens.Clear();
        _panel?.Close();
        _panel = null;
        _editor = null;
    }

    /// <summary>Esc: out of drag-in, else deselect, else leave (asking first if there are unsaved changes).</summary>
    private void Escape()
    {
        if (_dragIn) CancelDragIn();
        else if (_selection is not Selection.None) Select(new Selection.None());
        else _editor?.Back();
    }

    private void ToForm()
    {
        var draft = Draft;
        var isNew = _isNew;
        Close();
        _openForm(draft, isNew);
    }

    // ---- Selection ----

    private void Select(Selection selection)
    {
        if (_editor is null) return;
        _selection = selection;
        _editor.Show(selection switch
        {
            Selection.Window w => new EditorFocus.Window(w.Index),
            Selection.Region r => new EditorFocus.Assign($"Zone {ZonesLabel(r.Zones)} on {Short(r.Device)}"),
            Selection.Screen s => new EditorFocus.Custom($"{Short(s.Device)}'s layout", () => LayoutChooser(s.Device)),
            _ => new EditorFocus.Flock(),
        });
        Redraw();
        PositionPanel();
        _panel?.Activate();
    }

    /// <summary>The editor moved on by itself (a window removed, Test load, Cancel): follow it on screen.</summary>
    private void OnEditorFocus(EditorFocus focus)
    {
        _selection = focus switch
        {
            EditorFocus.Window w => new Selection.Window(w.Index),
            _ => new Selection.None(),
        };
        Redraw();
        PositionPanel();
    }

    private void OnRegionClicked(string device, int region)
    {
        if (!_screens.TryGetValue(device, out var s) || region >= s.Screen.Regions.Count) return;
        var r = s.Screen.Regions[region];
        Select(r.IsEmpty ? new Selection.Region(device, r.Zones) : new Selection.Window(r.Windows[0]));
    }

    // ---- Drawing ----

    private string LayoutFor(MonitorInfo m)
        => FlockDesign.LayoutOn(Draft, m.DeviceName, _monitors)
           ?? _screenLayouts.GetValueOrDefault(m.DeviceName)
           ?? ZoneLayout.ById(_zones.LayoutIdFor(m)).Id;

    private void Redraw()
    {
        if (_editor is null) return;
        var draft = Draft;
        var problems = FlockProblems.Find(draft);
        var values = draft.InputList.ToDictionary(i => i.Name, i => i.Kind == InputKind.Computed ? $"⟨{i.Name}⟩" : i.DefaultValue(),
            StringComparer.OrdinalIgnoreCase);

        DesignTileWindow TileWindow(int i)
        {
            var w = draft.Windows[i];
            var r = w.EffectiveRecipe();
            var detail = r.Kind switch
            {
                LaunchKind.Skip => "left out when loading",
                LaunchKind.AlreadyOpen => "uses a window that's already open",
                _ when r.Arguments.Length == 0 => r.Kind == LaunchKind.StoreApp ? "Store app" : "no arguments",
                _ => Placeholders.Expand(r.Arguments, values).Text,
            };
            return new DesignTileWindow(i, w.ProcessName, detail, _selection is Selection.Window s && s.Index == i,
                problems.Any(p => p.Window == i));
        }

        foreach (var m in _monitors)
        {
            if (!_surfaces.TryGetValue(m.DeviceName, out var surface)) continue;
            var layoutId = LayoutFor(m);
            var zones = _zones.ZonesFor(m, layoutId);
            var screen = FlockDesign.Screen(draft, m, layoutId, zones.Count, _monitors);
            _screens[m.DeviceName] = (screen, zones);

            var tiles = screen.Regions.Select((r, i) => new DesignTile(
                i, ZoneMath.Bounds(zones, r.Zones), ZonesLabel(r.Zones), r.Windows.Select(TileWindow).ToList(),
                IsSelected(m.DeviceName, r))).ToList();
            var chip = $"{m.ShortName} · {ZoneLayout.ById(layoutId).Name}"
                       + (_selection is Selection.Screen sc && sc.Device == m.DeviceName ? " · changing" : "");
            surface.Show(chip, tiles, screen.Floating.Select(TileWindow).ToList());
        }
    }

    private bool IsSelected(string device, DesignRegion r) => _selection switch
    {
        Selection.Window w => r.Windows.Contains(w.Index),
        Selection.Region s => s.Device == device && s.Zones.SequenceEqual(r.Zones),
        _ => false,
    };

    // ---- The panel's place ----

    /// <summary>Beside what's selected — to its right if there's room, else its left, else inside its right edge —
    /// on the same monitor; with nothing selected, at the right of the flock's main monitor.</summary>
    private void PositionPanel()
    {
        if (_panel is null || _monitors.Count == 0) return;
        var (monitor, anchor) = Anchor();
        var wa = monitor.WorkArea;
        _panel.FitTo(wa, monitor.Scale);
        var (w, h) = _panel.PixelSizeAt(monitor.Scale);
        var gap = (int)(16 * monitor.Scale);

        int x;
        if (anchor is not { } a) x = wa.Right - w - gap * 2;
        else if (a.Right + gap + w <= wa.Right) x = a.Right + gap;
        else if (a.X - gap - w >= wa.X) x = a.X - gap - w;
        else x = a.Right - w - gap;
        var y = anchor is { } b ? b.Y : wa.Y + (wa.Height - h) / 2;

        x = Math.Clamp(x, wa.X + gap, Math.Max(wa.X + gap, wa.Right - w - gap));
        y = Math.Clamp(y, wa.Y + gap, Math.Max(wa.Y + gap, wa.Bottom - h - gap));
        _panel.MoveTo(x, y);
        RaisePanel();
    }

    private (MonitorInfo Monitor, PixelRect? Anchor) Anchor()
    {
        MonitorInfo On(string device) => _monitors.FirstOrDefault(m => m.DeviceName == device) ?? _monitors[0];

        switch (_selection)
        {
            case Selection.Window w when w.Index < Draft.Windows.Count:
                foreach (var (device, (screen, zones)) in _screens)
                {
                    if (screen.Regions.FirstOrDefault(r => r.Windows.Contains(w.Index)) is { } r) return (On(device), ZoneMath.Bounds(zones, r.Zones));
                    if (screen.Floating.Contains(w.Index)) return (On(device), null);
                }
                break;
            case Selection.Region r when _screens.TryGetValue(r.Device, out var s):
                return (On(r.Device), ZoneMath.Bounds(s.Zones, r.Zones));
            case Selection.Screen s:
                var m = On(s.Device);
                return (m, new PixelRect(m.WorkArea.X + m.WorkArea.Width / 2 - 1, m.WorkArea.Y, 2, (int)(60 * m.Scale)));
        }

        // Nothing selected: the monitor most of the flock is on.
        var main = Draft.Windows
            .GroupBy(x => FlockDesign.MonitorOf(x, _monitors).DeviceName)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        return (main is null ? _monitors.FirstOrDefault(x => x.IsPrimary) ?? _monitors[0] : On(main), null);
    }

    /// <summary>The panel stays above the screens (all topmost) whatever was clicked last.</summary>
    private void RaisePanel()
    {
        if (_panel?.TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    // ---- Moving windows ----

    /// <summary>The region under the cursor: its monitor, region index and zones.</summary>
    private (MonitorInfo Monitor, int Region, int Zone)? HitAtCursor()
    {
        if (!Win32.GetCursorPos(out var p)) return null;
        foreach (var m in _monitors)
        {
            if (!m.Contains(p.x, p.y) || !_screens.TryGetValue(m.DeviceName, out var s)) continue;
            var zone = ZoneMath.HitTest(s.Zones, p.x, p.y, m.WorkArea);
            if (zone < 0) return null;
            var region = s.Screen.Regions
                .Select((r, i) => (r, i))
                .Where(x => x.r.Zones.Contains(zone))
                .OrderBy(x => x.r.Zones.Count)
                .Select(x => x.i)
                .DefaultIfEmpty(-1)
                .First();
            return region < 0 ? null : (m, region, zone);
        }
        return null;
    }

    private void HighlightUnderCursor()
    {
        var hit = HitAtCursor();
        foreach (var (device, surface) in _surfaces)
            surface.SetDropTarget(hit is { } h && h.Monitor.DeviceName == device ? h.Region : null);
    }

    private void ClearHighlight()
    {
        foreach (var s in _surfaces.Values) s.SetDropTarget(null);
    }

    /// <summary>A window's tile was dropped: into the region under the cursor — or with Ctrl, spanning from the zone
    /// it was in to the one dropped on (same monitor and layout).</summary>
    private void OnWindowDropped(int index, bool span)
    {
        var hit = HitAtCursor();
        ClearHighlight();
        if (_editor is null || hit is not { } h || index >= Draft.Windows.Count) return;

        var (screen, zones) = _screens[h.Monitor.DeviceName];
        var layoutId = screen.LayoutId;
        var target = screen.Regions[h.Region].Zones;
        var current = Draft.Windows[index].Zone;
        if (span && current is { Zones.Count: > 0 } && current.LayoutId == layoutId
            && FlockDesign.MonitorOf(Draft.Windows[index], _monitors).DeviceName == h.Monitor.DeviceName)
        {
            target = ZoneMath.Span(zones, current.Zones.Min(), h.Zone);
        }
        _editor.Apply(FlockDesign.Place(Draft, index, h.Monitor, layoutId, target, ZoneMath.Bounds(zones, target)));
        Select(new Selection.Window(index));
    }

    // ---- What starts where ----

    private void Assign(SavedWindow window, int? replacing)
    {
        if (_editor is null) return;
        if (replacing is { } i)
        {
            _editor.Apply(FlockDesign.Replace(Draft, i, window));
            Select(new Selection.Window(i));
            return;
        }
        if (_selection is not Selection.Region r || !_screens.TryGetValue(r.Device, out var s)) return;
        var monitor = _monitors.First(m => m.DeviceName == r.Device);
        _editor.Apply(FlockDesign.Add(Draft, FlockDesign.Placed(window, monitor, s.Screen.LayoutId, r.Zones, ZoneMath.Bounds(s.Zones, r.Zones))));
        Select(new Selection.Window(Draft.Windows.Count - 1));
    }

    // ---- Drag a real window in ----

    private void BeginDragIn()
    {
        if (_editor is null) return;
        _dragIn = true;
        _dragInWindow = IntPtr.Zero;
        _beforeDragIn = _selection;
        foreach (var s in _surfaces.Values)
            s.SetPassThrough(true, "Drag any window onto a zone to add it · Esc or Cancel in the panel to stop");
        _editor.Show(new EditorFocus.Custom("Drag a window in", () =>
        {
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(new TextBlock
            {
                Text = "Pick up any window by its title bar and drop it on a zone. It's added as it is — its app, how it "
                       + "was started — and goes into the zone you drop it on. The window itself stays where you leave it.",
                TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Palette.Muted,
            });
            body.Children.Add(SettingsUi.FlatButton("Cancel", CancelDragIn));
            return body;
        }));
        PositionPanel();
    }

    private void CancelDragIn()
    {
        EndDragIn();
        Select(_beforeDragIn);
    }

    private void EndDragIn()
    {
        if (!_dragIn) return;
        _dragIn = false;
        _dragInWindow = IntPtr.Zero;
        _dragInTimer.Stop();
        ClearHighlight();
        foreach (var s in _surfaces.Values) s.SetPassThrough(false);
    }

    private void OnMoveSizeStarted(IntPtr hwnd)
    {
        if (!_dragIn || !WindowQuery.IsCandidate(hwnd)) return;
        _dragInWindow = hwnd;
        _dragInTimer.Start();
    }

    private void OnMoveSizeEnded(IntPtr hwnd)
    {
        if (!_dragIn || hwnd != _dragInWindow || _editor is null) return;
        var hit = HitAtCursor();
        EndDragIn();
        if (hit is not { } h)
        {
            Notice?.Invoke("Drop it on a zone to add it.");
            Select(_beforeDragIn);
            return;
        }

        var (screen, zones) = _screens[h.Monitor.DeviceName];
        var target = screen.Regions[h.Region].Zones;
        if (_capture(hwnd, 0) is not { } window)
        {
            Notice?.Invoke("Flamingle can't add that window.");
            Select(_beforeDragIn);
            return;
        }
        _editor.Apply(FlockDesign.Add(Draft, FlockDesign.Placed(window, h.Monitor, screen.LayoutId, target, ZoneMath.Bounds(zones, target))));
        Select(new Selection.Window(Draft.Windows.Count - 1));
    }

    // ---- Panel parts ----

    /// <summary>Where window <paramref name="index"/> goes: its monitor's zones as a small map (click one to move it
    /// there, Ctrl+click to span to it), and the other monitors to move it to.</summary>
    private Control PlacementFor(int index)
    {
        var body = new StackPanel { Spacing = 8 };
        var window = Draft.Windows[index];
        var monitor = FlockDesign.MonitorOf(window, _monitors);
        if (!_screens.TryGetValue(monitor.DeviceName, out var s)) return body;
        var (screen, zones) = s;
        var zoned = !screen.Floating.Contains(index);

        body.Children.Add(new TextBlock
        {
            Text = zoned
                ? $"{monitor.ShortName} · zone{(window.Zone!.Zones.Count == 1 ? "" : "s")} {ZonesLabel(window.Zone.Zones)} of {ZoneLayout.ById(screen.LayoutId).Name}"
                : $"{window.PlaceText()} — not in a zone, so it goes back to its saved size and place. Click a zone to put it in one.",
            FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap,
        });

        // The map: the monitor's zones, scaled down to 240 DIP wide.
        var wa = monitor.WorkArea;
        const double mapWidth = 240;
        var mapHeight = mapWidth * wa.Height / Math.Max(1, wa.Width);
        var map = new Canvas { Width = mapWidth, Height = mapHeight, HorizontalAlignment = HorizontalAlignment.Left };
        map.Children.Add(new Border { Width = mapWidth, Height = mapHeight, CornerRadius = new CornerRadius(6), Background = Palette.NavBg });
        var mine = zoned ? window.Zone!.Zones : [];
        for (var z = 0; z < zones.Count; z++)
        {
            var r = zones[z];
            var zone = z;
            var on = mine.Contains(z);
            var cell = new Border
            {
                Width = Math.Max(4, r.Width * mapWidth / wa.Width - 3), Height = Math.Max(4, r.Height * mapHeight / wa.Height - 3),
                CornerRadius = new CornerRadius(4),
                Background = on ? Palette.AccentBrush : Palette.ButtonBg,
                BorderBrush = Palette.Line, BorderThickness = new Thickness(on ? 0 : 1),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = (z + 1).ToString(), FontSize = 11, Foreground = on ? Palette.OnAccent : Palette.Muted,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            ToolTip.SetTip(cell, "Click to move it here · Ctrl+click to span to here");
            cell.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                var span = e.KeyModifiers.HasFlag(KeyModifiers.Control) && mine.Count > 0;
                var target = span ? ZoneMath.Span(zones, mine.Min(), zone) : [zone];
                _editor?.Apply(FlockDesign.Place(Draft, index, monitor, screen.LayoutId, target, ZoneMath.Bounds(zones, target)));
                Select(new Selection.Window(index));
            };
            Canvas.SetLeft(cell, (r.X - wa.X) * mapWidth / wa.Width + 1.5);
            Canvas.SetTop(cell, (r.Y - wa.Y) * mapHeight / wa.Height + 1.5);
            map.Children.Add(cell);
        }
        body.Children.Add(map);

        // Other monitors: same zones (as near as their layout allows).
        var others = _monitors.Where(m => m.DeviceName != monitor.DeviceName).ToList();
        if (others.Count > 0)
        {
            var row = new WrapPanel();
            row.Children.Add(new TextBlock { Text = "Move to", FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            foreach (var other in others)
            {
                var to = other;
                var button = SettingsUi.FlatButton(other.ShortName, () => MoveToMonitor(index, to));
                button.Padding = new Thickness(10, 3);
                button.Margin = new Thickness(0, 0, 6, 0);
                row.Children.Add(button);
            }
            body.Children.Add(row);
        }
        body.Children.Add(new TextBlock { Text = "Or drag its tile on screen.", FontSize = 12, Foreground = Palette.Muted });
        return body;
    }

    private void MoveToMonitor(int index, MonitorInfo to)
    {
        if (_editor is null || !_screens.TryGetValue(to.DeviceName, out var s)) return;
        var window = Draft.Windows[index];
        var from = ZoneLayout.ById(window.Zone?.LayoutId);
        var layout = ZoneLayout.ById(s.Screen.LayoutId);
        List<int> zones = window.Zone is { } z
            ? z.Zones.Select(n => FlockDesign.MapZone(from, layout, n)).Distinct().ToList()
            : [0];
        _editor.Apply(FlockDesign.Place(Draft, index, to, layout.Id, zones, ZoneMath.Bounds(s.Zones, zones)));
        Select(new Selection.Window(index));
    }

    /// <summary>A monitor's layout: pick another and the flock's windows there move to the matching zones.</summary>
    private Control LayoutChooser(string device)
    {
        var body = new StackPanel { Spacing = 10 };
        var monitor = _monitors.First(m => m.DeviceName == device);
        var current = LayoutFor(monitor);
        body.Children.Add(new TextBlock
        {
            Text = "The windows here move to the zones that best match where they were. This only changes the saved flock — "
                   + "your layout on screen stays as it is until the flock loads.",
            FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap,
        });
        var grid = new WrapPanel();
        foreach (var layout in ZoneLayout.Templates)
        {
            var l = layout;
            var on = l.Id == current;
            var cell = new Button
            {
                Content = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new LayoutThumbnail(l, 88, 50, on),
                        new TextBlock { Text = l.Name, FontSize = 11, Foreground = on ? Palette.AccentBrush : Palette.Fg, TextWrapping = TextWrapping.Wrap, Width = 88 },
                    },
                },
                Background = on ? new SolidColorBrush(Palette.Accent, 0.12) : Brushes.Transparent,
                BorderBrush = on ? Palette.AccentBrush : Brushes.Transparent, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6), Margin = new Thickness(0, 0, 6, 6),
                VerticalAlignment = VerticalAlignment.Top,
            };
            cell.Click += (_, _) => SetLayout(monitor, l);
            grid.Children.Add(cell);
        }
        body.Children.Add(grid);
        return body;
    }

    private void SetLayout(MonitorInfo monitor, ZoneLayout to)
    {
        if (_editor is null) return;
        var from = ZoneLayout.ById(LayoutFor(monitor));
        _screenLayouts[monitor.DeviceName] = to.Id;
        _editor.Apply(FlockDesign.SetLayout(Draft, monitor.DeviceName, from, to, _monitors));
        Select(new Selection.Screen(monitor.DeviceName));
    }

    // ---- Helpers ----

    private static string ZonesLabel(IEnumerable<int> zones) => string.Join("+", zones.Order().Select(z => z + 1));

    private static string Short(string device) => device.TrimStart('\\', '.');
}
