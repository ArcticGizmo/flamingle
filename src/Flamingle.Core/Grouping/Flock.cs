namespace Flamingle.Core.Grouping;

/// <summary>One window that belongs to a flock. <see cref="ProcessId"/> guards against a recycled handle
/// when re-attaching after a restart.</summary>
public sealed record FlockMember(long Handle, uint ProcessId, string ProcessName, string Title);

/// <summary>A named set of windows that minimise / restore together.</summary>
public sealed class Flock
{
    public Flock(Guid id, string name, IEnumerable<FlockMember> members)
    {
        Id = id;
        Name = name;
        Members = members.ToList();
    }

    public Guid Id { get; }
    public string Name { get; set; }
    public List<FlockMember> Members { get; }

    /// <summary>When a flock was last acted on — the fallback target for the "toggle" hotkey.</summary>
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>Set for a flock built automatically from windows dropped into zones: the monitor device name
    /// it belongs to. Null for flocks the user picked by hand.</summary>
    public string? ZoneMonitor { get; set; }

    /// <summary>For a zone flock: the virtual desktop it belongs to, so each desktop's zoned windows form their
    /// own flock. Null for hand-picked flocks, and for zone flocks made before (or without) per-desktop flocks.</summary>
    public Guid? ZoneDesktop { get; set; }

    /// <summary>The user gave it its name. A zone flock's generated name ("Zones · DISPLAY1") follows it between
    /// monitors and desktops; a name the user chose is kept.</summary>
    public bool Named { get; set; }

    public bool Contains(long handle) => Members.Any(m => m.Handle == handle);
}
