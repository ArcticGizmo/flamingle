using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Saved;

namespace Flamingle.App.Services;

/// <summary>
/// Loads a saved flock: for each window, uses a matching window that's already open and not another flock's (as its
/// recipe allows) or starts
/// its app and waits for the new window to settle; then puts every window back — into its zones, or its saved rect
/// mapped onto today's monitors — maximised / minimised as saved, stacked in its saved order, on its virtual desktop;
/// and makes them a live flock. One load at a time; polled on the UI thread, never blocking on an app.
/// </summary>
internal sealed class RelaunchService : IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(200);

    private readonly ZoneContainerService _zones;
    private readonly FlockController _controller;
    private readonly Action<MonitorInfo, string> _applyLayout;
    private readonly Action<SavedFlock, IReadOnlyList<long>> _formFlock;
    private readonly DispatcherTimer _poll;
    private Run? _run;

    /// <param name="applyLayout">Switch a monitor's zone layout (the app remembers the choice).</param>
    /// <param name="formFlock">Make the placed windows (topmost first) a live flock.</param>
    public RelaunchService(ZoneContainerService zones, FlockController controller, Action<MonitorInfo, string> applyLayout,
        Action<SavedFlock, IReadOnlyList<long>> formFlock)
    {
        _zones = zones;
        _controller = controller;
        _applyLayout = applyLayout;
        _formFlock = formFlock;
        _poll = new DispatcherTimer { Interval = PollEvery };
        _poll.Tick += (_, _) => Poll();
    }

    /// <summary>A load that was refused straight away (one already running, or the flock has a problem).</summary>
    public event Action<string>? Notice;

    /// <summary>How a load went, when something didn't — it finishes seconds later, maybe after the user moved on.</summary>
    public event Action<Notification>? Notified;

    private enum State { Waiting, Found, Missed, Skipped }

    private sealed class Slot(PlannedWindow plan, DateTimeOffset deadline)
    {
        public PlannedWindow Plan { get; } = plan;
        public DateTimeOffset Deadline { get; } = deadline;
        public State State { get; set; } = State.Waiting;
        public long Handle { get; set; }
        public bool Reused { get; set; }
        public bool Launched { get; set; }
        public string? Why { get; set; }
        public string App => Plan.Window.ProcessName;
    }

    private sealed class Run(SavedFlock flock, string? onto, HashSet<long> before, IReadOnlyList<string> blank)
    {
        public SavedFlock Flock { get; } = flock;
        public string? Onto { get; } = onto;
        public IReadOnlyList<string> Blank { get; } = blank;
        public HashSet<long> Before { get; } = before;
        public List<Slot> Slots { get; } = [];
        public HashSet<long> Claimed { get; } = [];
        public WindowSettler Settler { get; } = new();
        public Dictionary<long, OpenWindow> Identities { get; } = [];
    }

    /// <summary>
    /// Load <paramref name="flock"/> with these input values. <paramref name="ontoDevice"/> (picked when loading from
    /// Settings, or a layout panel's monitor) moves a flock that was all on one monitor onto that one instead. Whatever
    /// is in the zones it fills is minimised first, and the loaded windows become a flock of their own.
    /// <paramref name="blank"/> names inputs that had no value, so the result can say so.
    /// </summary>
    public void Load(SavedFlock flock, IReadOnlyDictionary<string, string> values, string? ontoDevice = null,
        IReadOnlyList<string>? blank = null)
    {
        if (_run is not null)
        {
            Notice?.Invoke($"Still loading \"{_run.Flock.Name}\".");
            return;
        }
        if (FlockProblems.Find(flock) is { Count: > 0 } problems)
        {
            Notice?.Invoke($"\"{flock.Name}\" can't load: {problems[0].Message} Fix it in Settings › Flocks › Edit.");
            return;
        }

        var plan = LoadPlan.Build(flock, values);
        var open = WindowQuery.Candidates();
        var run = new Run(flock, ontoDevice, open.Select(w => w.Handle).ToHashSet(), blank ?? []);
        var now = DateTimeOffset.UtcNow;
        var inAnotherFlock = InAnotherFlock(run, plan.Windows.Select(p => p.Window));

        foreach (var p in plan.Windows)
        {
            var slot = new Slot(p, now + TimeSpan.FromSeconds(p.Recipe.WaitSeconds));
            run.Slots.Add(slot);
            if (p.Recipe.Kind == LaunchKind.Skip)
            {
                slot.State = State.Skipped;
                continue;
            }

            // "Ask" is treated as "use it" until there's a prompt to ask with.
            var reuse = p.Recipe.Kind == LaunchKind.AlreadyOpen || p.Recipe.IfOpen != IfAlreadyOpen.StartAnother;
            var unclaimed = open.Where(w => !run.Claimed.Contains(w.Handle)).ToList();
            var free = unclaimed.Where(w => !inAnotherFlock.Contains(w.Handle)).Select(w => Identity(run, w.Handle, w.Title));
            // A window another flock has (say this one, loaded onto the other monitor) is left to it: start another
            // instead. Only a window that's never started takes one from another flock, having no other way to load.
            var existing = WindowMatcher.Best(p, free)
                           ?? (p.Recipe.Launches ? null : WindowMatcher.Best(p, unclaimed.Select(w => Identity(run, w.Handle, w.Title))));
            if (reuse && existing is not null)
            {
                Claim(run, slot, existing.Handle);
                slot.Reused = true;
                continue;
            }
            if (!p.Recipe.Launches) continue;   // already-open only: wait for one to turn up

            if (AppLauncher.Start(p) is { } error)
            {
                slot.State = State.Missed;
                slot.Why = $"{slot.App} didn't start ({error})";
                continue;
            }
            slot.Launched = true;
        }

        _run = run;
        Poll();
        if (_run is not null) _poll.Start();
    }

    // ---- Waiting for windows ----

    private void Poll()
    {
        if (_run is not { } run) return;
        var waiting = run.Slots.Where(s => s.State == State.Waiting).ToList();
        if (waiting.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var open = WindowQuery.Candidates();
            foreach (var slot in waiting)
            {
                // A started app's window must be new; an already-open-only one can be any that turns up.
                var matches = open
                    .Where(w => !run.Claimed.Contains(w.Handle) && !(slot.Launched && run.Before.Contains(w.Handle)))
                    .Select(w => (Snapshot: w, Window: Identity(run, w.Handle, w.Title)))
                    .Where(x => WindowMatcher.Matches(slot.Plan, x.Window))
                    .ToList();
                // Observe every match each tick (the settle clock needs them all), then take the best settled one.
                var settled = matches.Where(x => run.Settler.Observe(x.Snapshot.Handle, x.Snapshot.Title, x.Snapshot.Bounds)).ToList();
                if (WindowMatcher.Best(slot.Plan, settled.Select(x => x.Window)) is { } found)
                {
                    Claim(run, slot, found.Handle);
                }
                else if (now >= slot.Deadline)
                {
                    slot.State = State.Missed;
                    slot.Why = slot.Launched
                        ? $"{slot.App} didn't show a window within {slot.Plan.Recipe.WaitSeconds} s"
                        : $"no open {slot.App} window turned up";
                }
            }
        }

        if (run.Slots.Any(s => s.State == State.Waiting)) return;
        _poll.Stop();
        Restore(run);
    }

    /// <summary>
    /// The open windows that belong to a flock this load mustn't take from: every flock but the zone flocks on the
    /// monitors whose zones it fills — those are minimised to make room anyway, so their windows are fair game.
    /// </summary>
    private HashSet<long> InAnotherFlock(Run run, IEnumerable<SavedWindow> windows)
    {
        var saved = windows.ToList();
        var monitors = Monitors.All();
        var onto = Onto(run, saved, monitors);
        var clearing = saved.Where(w => w.Zone is not null)
            .Select(w => (onto ?? PlacementMapper.Monitor(w.Placement.Monitor, w.Placement.WorkArea, monitors)).DeviceName)
            .Distinct()
            .Select(_controller.CurrentZoneFlock)
            .OfType<Flock>()
            .ToHashSet();
        return run.Before.Where(h => _controller.FlockOf(h) is { } g && !clearing.Contains(g)).ToHashSet();
    }

    private static void Claim(Run run, Slot slot, long handle)
    {
        slot.State = State.Found;
        slot.Handle = handle;
        run.Claimed.Add(handle);
    }

    /// <summary>Who owns a window (looked up once per load), with its current title.</summary>
    private static OpenWindow Identity(Run run, long handle, string title)
    {
        if (!run.Identities.TryGetValue(handle, out var w))
            run.Identities[handle] = w = WindowIdentity.Of((IntPtr)handle);
        return w.Title == title ? w : w with { Title = title };
    }

    // ---- Putting them back ----

    /// <summary>Step 1: un-minimise / un-maximise, so they can be moved.</summary>
    private void Restore(Run run)
    {
        var found = run.Slots.Where(s => s.State == State.Found && Win32.IsWindow((IntPtr)s.Handle)).ToList();
        foreach (var s in found)
        {
            var hwnd = (IntPtr)s.Handle;
            if (!Win32.IsIconic(hwnd) && !Win32.IsZoomed(hwnd)) continue;
            _controller.Suppress(s.Handle);
            Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);
        }
        DispatcherTimer.RunOnce(() => Position(run, found), Step);
    }

    /// <summary>Step 2: into its zones, or its saved rect on today's monitors; onto its virtual desktop.</summary>
    private void Position(Run run, List<Slot> found)
    {
        var monitors = Monitors.All();
        var onto = Onto(run, found.Select(s => s.Plan.Window), monitors);
        var layoutsSet = new HashSet<string>();
        MonitorInfo MonitorFor(Slot s)
            => onto ?? PlacementMapper.Monitor(s.Plan.Window.Placement.Monitor, s.Plan.Window.Placement.WorkArea, monitors);

        var cleared = ClearZones(found.Where(s => s.Plan.Window.Zone is not null).Select(MonitorFor), run.Claimed);

        foreach (var s in found)
        {
            var hwnd = (IntPtr)s.Handle;
            if (!Win32.IsWindow(hwnd)) continue;
            var saved = s.Plan.Window;
            var monitor = MonitorFor(s);

            if (saved.Zone is { } zone)
            {
                if (layoutsSet.Add(monitor.DeviceName) && _zones.LayoutIdFor(monitor) != zone.LayoutId)
                    _applyLayout(monitor, zone.LayoutId);
                // Where the zones were cleared, the loaded windows become a flock of their own (in Finish) rather than
                // joining the zone flock that was just minimised.
                _zones.SnapInto(hwnd, monitor, zone.Zones, joinZoneFlock: !cleared.Contains(monitor.DeviceName));
            }
            else
            {
                var rect = PlacementMapper.Rect(saved.Placement.Restored, saved.Placement.WorkArea, monitor.WorkArea);
                Move(hwnd, rect);
                // Landing on a monitor with another DPI rescales the window once it arrives; size it again after.
                DispatcherTimer.RunOnce(() => Move(hwnd, rect), Step);
            }

            if (saved.Desktop is { } desktop && VirtualDesktops.DesktopOf(hwnd) is { } now && now != desktop)
                VirtualDesktops.MoveTo(hwnd, desktop);
        }
        DispatcherTimer.RunOnce(() => Finish(run, found), Step * 2);
    }

    /// <summary>Step 3: maximise, restack (saved order, topmost in front), minimise, and form the flock.</summary>
    private void Finish(Run run, List<Slot> found)
    {
        var live = found.Where(s => Win32.IsWindow((IntPtr)s.Handle)).OrderBy(s => s.Plan.Window.ZIndex).ToList();

        foreach (var s in live.Where(s => s.Plan.Window is { Zone: null, Placement.Maximised: true }))
        {
            _controller.Suppress(s.Handle);
            Win32.ShowWindowAsync((IntPtr)s.Handle, Win32.SW_MAXIMIZE);
        }

        if (live.Count > 0)
        {
            Win32.SetForegroundWindow((IntPtr)live[0].Handle);
            for (var i = 1; i < live.Count; i++)
            {
                Win32.SetWindowPos((IntPtr)live[i].Handle, (IntPtr)live[i - 1].Handle, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER | Win32.SWP_ASYNCWINDOWPOS);
            }
        }

        foreach (var s in live.Where(s => s.Plan.Window.Placement.Minimised))
        {
            _controller.Suppress(s.Handle);
            Win32.ShowWindowAsync((IntPtr)s.Handle, Win32.SW_MINIMIZE);
        }

        if (live.Count > 0) _formFlock(run.Flock, live.Select(s => s.Handle).ToList());
        _run = null;
        if (Summary(run, live) is { } summary) Notified?.Invoke(summary);
    }

    /// <summary>
    /// Make room on the monitors whose zones this load fills: each one's zone flock (on the desktop being looked at)
    /// is minimised as a unit — it stays its own flock, to restore later — after letting go of any of its windows
    /// this load is reusing. Returns the monitors that had something minimised.
    /// </summary>
    private HashSet<string> ClearZones(IEnumerable<MonitorInfo> monitors, IReadOnlySet<long> claimed)
    {
        var cleared = new HashSet<string>();
        foreach (var device in monitors.Select(m => m.DeviceName).Distinct())
        {
            if (_controller.CurrentZoneFlock(device) is not { } there) continue;
            foreach (var reused in there.Members.Select(m => m.Handle).Where(claimed.Contains).ToList())
                _controller.LeaveZoneFlock((IntPtr)reused);
            if (there.Members.Count == 0) continue;
            _controller.Minimise(there);
            cleared.Add(device);
        }
        return cleared;
    }

    /// <summary>The monitor to load onto (chosen when loading from Settings, or a layout panel's) — only when the
    /// whole flock was on one monitor.</summary>
    private static MonitorInfo? Onto(Run run, IEnumerable<SavedWindow> windows, IReadOnlyList<MonitorInfo> monitors)
    {
        if (run.Onto is null || windows.Select(w => w.Placement.Monitor).Distinct().Count() != 1) return null;
        return monitors.FirstOrDefault(m => m.DeviceName == run.Onto);
    }

    private static void Move(IntPtr hwnd, PixelRect rect)
    {
        if (!Win32.IsWindow(hwnd)) return;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>What went wrong with a load, or null if nothing did — the windows turning up says it worked.</summary>
    private static Notification? Summary(Run run, List<Slot> live)
    {
        var problems = run.Slots.Where(s => s.State == State.Missed).Select(s => s.Why + ".").ToList();
        if (run.Blank.Count > 0)
            problems.Add($"Left {string.Join(", ", run.Blank)} blank — computed inputs aren't worked out yet.");

        if (live.Count == 0)
            return new($"Couldn't load \"{run.Flock.Name}\"",
                problems.Count > 0 ? string.Join(" ", problems) : "None of its windows could be found or started.", Warning: true);
        if (problems.Count == 0) return null;

        var started = live.Count(s => !s.Reused);
        var reused = live.Count(s => s.Reused);
        var parts = new List<string>();
        if (started > 0) parts.Add($"{started} started");
        if (reused > 0) parts.Add($"{reused} already open");
        return new($"Loaded \"{run.Flock.Name}\" ({string.Join(", ", parts)})", string.Join(" ", problems), Warning: true);
    }

    public void Dispose() => _poll.Stop();
}
