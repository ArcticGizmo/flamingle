using Flamingle.Core.Grouping;

namespace Flamingle.Core.Persistence;

/// <summary>
/// The on-disk form of the live flocks (<c>flocks.json</c>). Phase 1 flocks are made of live window
/// handles, so this only survives a Flamingle restart while the windows stay open — on load, members whose
/// handle is gone (or now belongs to a different process) are dropped. Saved layouts that relaunch apps
/// are Phase 3.
/// </summary>
public sealed record FlockSnapshot(List<FlockSnapshot.Group> Groups)
{
    public sealed record Group(
        Guid Id, string Name, DateTimeOffset LastUsed, List<FlockMember> Members, string? ZoneMonitor = null,
        Guid? ZoneDesktop = null);

    public static FlockSnapshot Empty { get; } = new([]);

    public static FlockSnapshot From(FlockRegistry registry)
        => new(registry.Groups
            .Select(g => new Group(g.Id, g.Name, g.LastUsed, [.. g.Members], g.ZoneMonitor, g.ZoneDesktop))
            .ToList());

    /// <summary>Rebuild the groups, keeping only members <paramref name="isAlive"/> accepts.</summary>
    public IEnumerable<Flock> ToGroups(Func<FlockMember, bool> isAlive)
        => Groups.Select(g => new Flock(g.Id, g.Name, g.Members.Where(isAlive))
        {
            LastUsed = g.LastUsed,
            ZoneMonitor = g.ZoneMonitor,
            ZoneDesktop = g.ZoneDesktop,
        });
}
