using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Settings;

namespace Flamingle.App.Services;

/// <summary>
/// Minimises / restores flocks and keeps them in step with the desktop. Drives the member windows through
/// async Win32 calls (a hung app can't block us), records Z-order and positions on the way down so the way
/// up puts everything back, and — when <see cref="AppSettings.LinkedMinimise"/> is on — follows the user
/// minimising or restoring any one member. Lives on the UI thread.
/// </summary>
internal sealed class FlockController
{
    // How long after a restore to re-stack and re-position (lets the async restores land first).
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(200);

    // How long to give a minimise before checking for members that ignored it.
    private static readonly TimeSpan VerifyDelay = TimeSpan.FromMilliseconds(800);

    private const int PositionTolerance = 2;

    private readonly FlockRegistry _registry;
    private readonly Func<AppSettings> _settings;
    private readonly EchoSuppressor _echo = new();
    private readonly WinEventHooks _hooks;

    // What each minimised flock looked like just before it went down.
    private readonly Dictionary<Guid, MinimisedState> _minimised = [];

    // Bumped on every action, so a delayed settle/verify can tell it's been superseded.
    private readonly Dictionary<Guid, int> _generation = [];

    private sealed record MinimisedState(IReadOnlyList<long> OrderTopFirst, Dictionary<long, PixelRect> Bounds);

    public FlockController(FlockRegistry registry, WinEventHooks hooks, Func<AppSettings> settings)
    {
        _registry = registry;
        _hooks = hooks;
        _settings = settings;
    }

    /// <summary>A short message for the user (shown as a toast).</summary>
    public event Action<string>? Notice;

    /// <summary>The last app window the user focused (never Flamingle's own, the taskbar or tray). Zero after
    /// the user clicks the bare desktop.</summary>
    public long LastFocus { get; private set; }

    /// <summary>Subscribe to the (shared) hooks. The caller starts them.</summary>
    public void Start()
    {
        _hooks.ForegroundChanged += OnForegroundChanged;
        _hooks.MinimiseStarted += OnMinimiseStarted;
        _hooks.MinimiseEnded += OnMinimiseEnded;
        _hooks.Destroyed += hwnd => _registry.RemoveWindow(hwnd);

        var fg = Win32.GetForegroundWindow();
        if (WindowQuery.IsCandidate(fg)) LastFocus = fg;
    }

    // ---- Commands ----

    /// <summary>Toggle the flock holding the focused window, else the most recently used one.</summary>
    public void ToggleFocused()
    {
        Prune();
        var group = _registry.FindByWindow(LastFocus) ?? _registry.MostRecentlyUsed;
        if (group is null) Notice?.Invoke("No flocks yet — press the new-flock hotkey to make one.");
        else Toggle(group);
    }

    public void ToggleSlot(int slot)
    {
        Prune();
        if (_registry.BySlot(slot) is { } group) Toggle(group);
        else Notice?.Invoke($"No flock in slot {slot}.");
    }

    public void Toggle(Flock group)
    {
        var members = LiveMembers(group);
        if (members.Count == 0) return;

        var focusInFlock = group.Contains(LastFocus) && !Win32.IsIconic((IntPtr)LastFocus);
        var action = ToggleDecision.Decide(members.Select(h => Win32.IsIconic((IntPtr)h)).ToList(), focusInFlock);

        if (action == FlockAction.Minimise) Minimise(group);
        else Restore(group);
    }

    /// <summary>Minimise every member, remembering Z-order and positions for the restore.</summary>
    /// <param name="alreadyMinimising">A member the user is minimising themselves (linked mode) — left alone.</param>
    public void Minimise(Flock group, long alreadyMinimising = 0)
    {
        var members = LiveMembers(group);
        if (members.Count == 0) return;

        var order = ZOrder.Sort(members, WindowQuery.ZOrderTopFirst());
        var bounds = new Dictionary<long, PixelRect>();
        foreach (var h in members)
        {
            var hwnd = (IntPtr)h;
            if (!Win32.IsIconic(hwnd) && !Win32.IsZoomed(hwnd) && WindowQuery.WindowRect(hwnd) is { } r)
                bounds[h] = r;
        }
        _minimised[group.Id] = new MinimisedState(order, bounds);

        foreach (var h in order)
        {
            if (h == alreadyMinimising || Win32.IsIconic((IntPtr)h)) continue;
            _echo.Suppress(h);
            Win32.ShowWindowAsync((IntPtr)h, Win32.SW_MINIMIZE);
        }

        _registry.Touch(group);
        var gen = Bump(group);
        DispatcherTimer.RunOnce(() => VerifyMinimised(group, gen), VerifyDelay);
    }

    /// <summary>Restore every member and stack them back in their old Z-order, in front of everything.</summary>
    /// <param name="top">A member that should end up on top — e.g. the one the user just restored.</param>
    public void Restore(Flock group, long top = 0)
    {
        var members = LiveMembers(group);
        if (members.Count == 0) return;

        _minimised.Remove(group.Id, out var state);
        var order = ZOrder.Sort(members, state?.OrderTopFirst ?? WindowQuery.ZOrderTopFirst());
        if (top != 0) order = ZOrder.BringToFront(order, top);

        // Bottom-most first, so the top member is restored (and activated) last.
        foreach (var h in order.Reverse())
        {
            if (h == top || !Win32.IsIconic((IntPtr)h)) continue;
            _echo.Suppress(h);
            Win32.ShowWindowAsync((IntPtr)h, Win32.SW_RESTORE);
        }
        Win32.SetForegroundWindow((IntPtr)order[0]);

        _registry.Touch(group);
        var gen = Bump(group);
        DispatcherTimer.RunOnce(() => Settle(group, order, state, gen), SettleDelay);
    }

    public Flock Create(string name, IEnumerable<FlockMember> members)
    {
        var group = _registry.Create(name, members);
        Notice?.Invoke($"Created \"{group.Name}\" ({group.Members.Count} windows) — slot {_registry.SlotOf(group)}.");
        return group;
    }

    // ---- Zone flocks ----

    /// <summary>
    /// A window was dropped into zones on <paramref name="monitor"/>: add it to that monitor's zone flock
    /// (creating it on first use), so the zoned layout minimises / restores as one. Windows in a hand-picked
    /// flock stay where they are; a window moving between monitors moves between zone flocks.
    /// </summary>
    public void JoinZoneFlock(IntPtr hwnd, MonitorInfo monitor)
    {
        if (!_settings().ZonesFormFlock || !WindowQuery.IsCandidate(hwnd)) return;

        var current = _registry.FindByWindow(hwnd);
        if (current is { ZoneMonitor: null }) return;
        if (current?.ZoneMonitor == monitor.DeviceName) return;

        var member = WindowQuery.ToMember(WindowQuery.Snapshot(hwnd));
        if (_registry.ForZoneMonitor(monitor.DeviceName) is { } group)
        {
            _registry.Add(group, member);
        }
        else
        {
            group = _registry.Create($"Zones · {monitor.ShortName}", [member], monitor.DeviceName);
            Notice?.Invoke($"Started a zone flock on {monitor.ShortName} (slot {_registry.SlotOf(group)}). " +
                           "Windows you drop into zones there will minimise and restore together.");
        }
    }

    /// <summary>A window was dragged out of its zones: drop it from its zone flock (hand-picked flocks
    /// are left alone).</summary>
    public void LeaveZoneFlock(IntPtr hwnd)
    {
        if (_registry.FindByWindow(hwnd) is { ZoneMonitor: not null })
            _registry.RemoveWindow(hwnd);
    }

    // ---- Follow-ups ----

    private void Settle(Flock group, IReadOnlyList<long> order, MinimisedState? state, int gen)
    {
        if (CurrentGeneration(group) != gen) return;   // the user has already done something else

        if (_settings().CorrectPositionsOnRestore && state is not null)
        {
            foreach (var (h, want) in state.Bounds)
            {
                var hwnd = (IntPtr)h;
                if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd) || Win32.IsZoomed(hwnd)) continue;
                if (WindowQuery.WindowRect(hwnd) is not { } now || now.RoughlyEquals(want, PositionTolerance)) continue;

                _echo.Suppress(h);
                Win32.SetWindowPos(hwnd, IntPtr.Zero, want.X, want.Y, want.Width, want.Height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
            }
        }

        // Re-stack: the top member in front, each following member directly beneath the one before it.
        var live = order.Where(h => Win32.IsWindow((IntPtr)h)).ToList();
        if (live.Count == 0) return;

        Win32.SetForegroundWindow((IntPtr)live[0]);
        for (var i = 1; i < live.Count; i++)
        {
            _echo.Suppress(live[i]);
            Win32.SetWindowPos((IntPtr)live[i], (IntPtr)live[i - 1], 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER
                | Win32.SWP_ASYNCWINDOWPOS);
        }

        var stuck = live.Where(h => Win32.IsIconic((IntPtr)h)).ToList();
        if (stuck.Count > 0)
            Notice?.Invoke($"Couldn't restore {Describe(group, stuck)}.{ElevationHint}");
    }

    private void VerifyMinimised(Flock group, int gen)
    {
        if (CurrentGeneration(group) != gen) return;
        var stuck = group.Members.Select(m => m.Handle)
            .Where(h => Win32.IsWindow((IntPtr)h) && !Win32.IsIconic((IntPtr)h))
            .ToList();
        if (stuck.Count > 0)
            Notice?.Invoke($"Couldn't minimise {Describe(group, stuck)}.{ElevationHint}");
    }

    private const string ElevationHint =
        " If it's running as administrator, Flamingle has to be too.";

    // ---- Hook handlers ----

    private void OnForegroundChanged(IntPtr hwnd)
    {
        if (WindowQuery.IsCandidate(hwnd)) LastFocus = hwnd;
        else if (WindowQuery.IsDesktop(hwnd)) LastFocus = 0;
        // Anything else (taskbar, tray, our own windows) leaves the last real app window as the focus.
    }

    private void OnMinimiseStarted(IntPtr hwnd)
    {
        if (!_settings().LinkedMinimise || _echo.IsSuppressed(hwnd)) return;
        if (_registry.FindByWindow(hwnd) is { } group)
            Minimise(group, alreadyMinimising: hwnd);
    }

    private void OnMinimiseEnded(IntPtr hwnd)
    {
        if (!_settings().LinkedMinimise || _echo.IsSuppressed(hwnd)) return;
        if (_registry.FindByWindow(hwnd) is { } group)
            Restore(group, top: hwnd);
    }

    // ---- Helpers ----

    private void Prune() => _registry.Prune(WindowQuery.IsAlive);

    private List<long> LiveMembers(Flock group)
    {
        Prune();
        return group.Members.Select(m => m.Handle).ToList();
    }

    private int Bump(Flock group)
    {
        var gen = CurrentGeneration(group) + 1;
        _generation[group.Id] = gen;
        return gen;
    }

    private int CurrentGeneration(Flock group) => _generation.GetValueOrDefault(group.Id);

    private static string Describe(Flock group, IEnumerable<long> handles)
    {
        var names = handles
            .Select(h => group.Members.FirstOrDefault(m => m.Handle == h))
            .Where(m => m is not null)
            .Select(m => string.IsNullOrEmpty(m!.ProcessName) ? m.Title : m.ProcessName)
            .Distinct();
        return string.Join(", ", names);
    }
}
