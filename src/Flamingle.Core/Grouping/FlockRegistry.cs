namespace Flamingle.Core.Grouping;

/// <summary>
/// The set of live flocks. Enforces the one-flock-per-window rule (adding a window to a new flock takes
/// it out of its old one) and drops a flock once it has no members left. Raises <see cref="Changed"/>
/// after every mutation so the tray menu and the on-disk copy can follow.
/// </summary>
public sealed class FlockRegistry
{
    private readonly List<Flock> _groups = [];
    private readonly TimeProvider _time;

    public FlockRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public event Action? Changed;

    /// <summary>In creation order — the order gives each flock its slot (1-based) for the slot hotkeys.</summary>
    public IReadOnlyList<Flock> Groups => _groups;

    public Flock Create(string name, IEnumerable<FlockMember> members, string? zoneMonitor = null,
        Guid? zoneDesktop = null)
    {
        var list = members.DistinctBy(m => m.Handle).ToList();
        foreach (var m in list)
            RemoveWindowSilently(m.Handle);

        var group = new Flock(Guid.NewGuid(), name, list)
        {
            LastUsed = _time.GetUtcNow(),
            ZoneMonitor = zoneMonitor,
            ZoneDesktop = zoneMonitor is null ? null : zoneDesktop,
        };
        _groups.Add(group);
        Changed?.Invoke();
        return group;
    }

    /// <summary>Add a window to an existing flock (taking it out of any other). No-op if it's already there.</summary>
    public void Add(Flock group, FlockMember member)
    {
        if (group.Contains(member.Handle) || !_groups.Contains(group)) return;
        RemoveWindowSilently(member.Handle);
        // Removing it elsewhere can't empty *this* group, so it's still registered.
        group.Members.Add(member);
        Changed?.Invoke();
    }

    /// <summary>The zone flock for a monitor on one virtual desktop, if there is one. With a null
    /// <paramref name="desktop"/> (per-desktop zone flocks off) any zone flock on the monitor will do.</summary>
    public Flock? ForZone(string deviceName, Guid? desktop)
        => _groups.FirstOrDefault(g => g.ZoneMonitor == deviceName && (desktop is null || g.ZoneDesktop == desktop));

    /// <summary>Every zone flock on a monitor (one per virtual desktop).</summary>
    public IEnumerable<Flock> ZoneFlocksOn(string deviceName) => _groups.Where(g => g.ZoneMonitor == deviceName);

    /// <summary>Pin an existing zone flock to a virtual desktop (zone flocks saved before they were per desktop).</summary>
    public void SetZoneDesktop(Flock group, Guid desktop, string newName)
    {
        if (group.ZoneMonitor is null || group.ZoneDesktop == desktop) return;
        group.ZoneDesktop = desktop;
        group.Name = newName;
        Changed?.Invoke();
    }

    /// <summary>A zone layout moved to another monitor: its zone flocks (one per desktop) follow, renamed.</summary>
    public void RehomeZoneFlocks(string from, string to, Func<Flock, string> nameFor)
    {
        if (from == to) return;
        var moving = ZoneFlocksOn(from).ToList();
        if (moving.Count == 0) return;
        foreach (var group in moving)
        {
            group.ZoneMonitor = to;
            group.Name = nameFor(group);
        }
        Changed?.Invoke();
    }

    /// <summary>Two zone layouts swapped monitors: each monitor's zone flocks move to the other, renamed.
    /// <paramref name="nameFor"/> sees each flock already on its new monitor.</summary>
    public void SwapZoneFlocks(string a, string b, Func<Flock, string> nameFor)
    {
        if (a == b) return;
        var fromA = ZoneFlocksOn(a).ToList();
        var fromB = ZoneFlocksOn(b).ToList();
        if (fromA.Count == 0 && fromB.Count == 0) return;
        foreach (var group in fromA) group.ZoneMonitor = b;
        foreach (var group in fromB) group.ZoneMonitor = a;
        foreach (var group in fromA.Concat(fromB)) group.Name = nameFor(group);
        Changed?.Invoke();
    }

    /// <summary>Replace everything (used when loading from disk). Empty groups are dropped.</summary>
    public void Load(IEnumerable<Flock> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups.Where(g => g.Members.Count > 0));
        Changed?.Invoke();
    }

    public Flock? FindByWindow(long handle) => _groups.FirstOrDefault(g => g.Contains(handle));

    public Flock? Find(Guid id) => _groups.FirstOrDefault(g => g.Id == id);

    /// <summary>The flock in a 1-based slot, or null.</summary>
    public Flock? BySlot(int slot) => slot >= 1 && slot <= _groups.Count ? _groups[slot - 1] : null;

    public int SlotOf(Flock group) => _groups.IndexOf(group) + 1;

    public Flock? MostRecentlyUsed => _groups.MaxBy(g => g.LastUsed);

    /// <summary>Mark a flock as just used. Doesn't raise <see cref="Changed"/> — nothing visible changes.</summary>
    public void Touch(Flock group) => group.LastUsed = _time.GetUtcNow();

    /// <summary>Remove a window from whichever flock holds it. True if anything changed.</summary>
    public bool RemoveWindow(long handle)
    {
        if (!RemoveWindowSilently(handle)) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drop every member that's no longer alive. True if anything changed.</summary>
    public bool Prune(Func<FlockMember, bool> isAlive)
    {
        var changed = false;
        foreach (var g in _groups)
            changed |= g.Members.RemoveAll(m => !isAlive(m)) > 0;
        changed |= _groups.RemoveAll(g => g.Members.Count == 0) > 0;

        if (changed) Changed?.Invoke();
        return changed;
    }

    public void Rename(Flock group, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || group.Name == name) return;
        group.Name = name.Trim();
        Changed?.Invoke();
    }

    public void Dissolve(Flock group)
    {
        if (_groups.Remove(group))
            Changed?.Invoke();
    }

    private bool RemoveWindowSilently(long handle)
    {
        var group = FindByWindow(handle);
        if (group is null) return false;

        group.Members.RemoveAll(m => m.Handle == handle);
        if (group.Members.Count == 0)
            _groups.Remove(group);
        return true;
    }
}
