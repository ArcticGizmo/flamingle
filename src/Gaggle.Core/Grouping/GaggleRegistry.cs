namespace Gaggle.Core.Grouping;

/// <summary>
/// The set of live gaggles. Enforces the one-gaggle-per-window rule (adding a window to a new gaggle takes
/// it out of its old one) and drops a gaggle once it has no members left. Raises <see cref="Changed"/>
/// after every mutation so the tray menu and the on-disk copy can follow.
/// </summary>
public sealed class GaggleRegistry
{
    private readonly List<GaggleGroup> _groups = [];
    private readonly TimeProvider _time;

    public GaggleRegistry(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public event Action? Changed;

    /// <summary>In creation order — the order gives each gaggle its slot (1-based) for the slot hotkeys.</summary>
    public IReadOnlyList<GaggleGroup> Groups => _groups;

    public GaggleGroup Create(string name, IEnumerable<GaggleMember> members, string? zoneMonitor = null)
    {
        var list = members.DistinctBy(m => m.Handle).ToList();
        foreach (var m in list)
            RemoveWindowSilently(m.Handle);

        var group = new GaggleGroup(Guid.NewGuid(), name, list) { LastUsed = _time.GetUtcNow(), ZoneMonitor = zoneMonitor };
        _groups.Add(group);
        Changed?.Invoke();
        return group;
    }

    /// <summary>Add a window to an existing gaggle (taking it out of any other). No-op if it's already there.</summary>
    public void Add(GaggleGroup group, GaggleMember member)
    {
        if (group.Contains(member.Handle) || !_groups.Contains(group)) return;
        RemoveWindowSilently(member.Handle);
        // Removing it elsewhere can't empty *this* group, so it's still registered.
        group.Members.Add(member);
        Changed?.Invoke();
    }

    /// <summary>The zone gaggle for a monitor, if there is one.</summary>
    public GaggleGroup? ForZoneMonitor(string deviceName) => _groups.FirstOrDefault(g => g.ZoneMonitor == deviceName);

    /// <summary>A zone layout moved to another monitor: its zone gaggle follows (and is renamed).</summary>
    public void RehomeZoneGaggle(string from, string to, string newName)
    {
        if (from == to || ForZoneMonitor(from) is not { } group) return;
        group.ZoneMonitor = to;
        group.Name = newName;
        Changed?.Invoke();
    }

    /// <summary>Replace everything (used when loading from disk). Empty groups are dropped.</summary>
    public void Load(IEnumerable<GaggleGroup> groups)
    {
        _groups.Clear();
        _groups.AddRange(groups.Where(g => g.Members.Count > 0));
        Changed?.Invoke();
    }

    public GaggleGroup? FindByWindow(long handle) => _groups.FirstOrDefault(g => g.Contains(handle));

    public GaggleGroup? Find(Guid id) => _groups.FirstOrDefault(g => g.Id == id);

    /// <summary>The gaggle in a 1-based slot, or null.</summary>
    public GaggleGroup? BySlot(int slot) => slot >= 1 && slot <= _groups.Count ? _groups[slot - 1] : null;

    public int SlotOf(GaggleGroup group) => _groups.IndexOf(group) + 1;

    public GaggleGroup? MostRecentlyUsed => _groups.MaxBy(g => g.LastUsed);

    /// <summary>Mark a gaggle as just used. Doesn't raise <see cref="Changed"/> — nothing visible changes.</summary>
    public void Touch(GaggleGroup group) => group.LastUsed = _time.GetUtcNow();

    /// <summary>Remove a window from whichever gaggle holds it. True if anything changed.</summary>
    public bool RemoveWindow(long handle)
    {
        if (!RemoveWindowSilently(handle)) return false;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Drop every member that's no longer alive. True if anything changed.</summary>
    public bool Prune(Func<GaggleMember, bool> isAlive)
    {
        var changed = false;
        foreach (var g in _groups)
            changed |= g.Members.RemoveAll(m => !isAlive(m)) > 0;
        changed |= _groups.RemoveAll(g => g.Members.Count == 0) > 0;

        if (changed) Changed?.Invoke();
        return changed;
    }

    public void Rename(GaggleGroup group, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || group.Name == name) return;
        group.Name = name.Trim();
        Changed?.Invoke();
    }

    public void Dissolve(GaggleGroup group)
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
