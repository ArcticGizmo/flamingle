using Gaggle.Core.Grouping;

namespace Gaggle.Core.Persistence;

/// <summary>
/// The on-disk form of the live gaggles (<c>gaggles.json</c>). Phase 1 gaggles are made of live window
/// handles, so this only survives a Gaggle restart while the windows stay open — on load, members whose
/// handle is gone (or now belongs to a different process) are dropped. Saved layouts that relaunch apps
/// are Phase 3.
/// </summary>
public sealed record GaggleSnapshot(List<GaggleSnapshot.Group> Groups)
{
    public sealed record Group(
        Guid Id, string Name, DateTimeOffset LastUsed, List<GaggleMember> Members, string? ZoneMonitor = null);

    public static GaggleSnapshot Empty { get; } = new([]);

    public static GaggleSnapshot From(GaggleRegistry registry)
        => new(registry.Groups
            .Select(g => new Group(g.Id, g.Name, g.LastUsed, [.. g.Members], g.ZoneMonitor))
            .ToList());

    /// <summary>Rebuild the groups, keeping only members <paramref name="isAlive"/> accepts.</summary>
    public IEnumerable<GaggleGroup> ToGroups(Func<GaggleMember, bool> isAlive)
        => Groups.Select(g => new GaggleGroup(g.Id, g.Name, g.Members.Where(isAlive))
        {
            LastUsed = g.LastUsed,
            ZoneMonitor = g.ZoneMonitor,
        });
}
