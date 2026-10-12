namespace Gaggle.Core.Grouping;

/// <summary>One window that belongs to a gaggle. <see cref="ProcessId"/> guards against a recycled handle
/// when re-attaching after a restart.</summary>
public sealed record GaggleMember(long Handle, uint ProcessId, string ProcessName, string Title);

/// <summary>A named set of windows that minimise / restore together.</summary>
public sealed class GaggleGroup
{
    public GaggleGroup(Guid id, string name, IEnumerable<GaggleMember> members)
    {
        Id = id;
        Name = name;
        Members = members.ToList();
    }

    public Guid Id { get; }
    public string Name { get; set; }
    public List<GaggleMember> Members { get; }

    /// <summary>When a gaggle was last acted on — the fallback target for the "toggle" hotkey.</summary>
    public DateTimeOffset LastUsed { get; set; }

    public bool Contains(long handle) => Members.Any(m => m.Handle == handle);
}
