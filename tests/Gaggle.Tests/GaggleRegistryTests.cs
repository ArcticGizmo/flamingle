using Gaggle.Core.Grouping;

namespace Gaggle.Tests;

public class GaggleRegistryTests
{
    private static GaggleMember M(long handle, uint pid = 1) => new(handle, pid, $"app{handle}", $"Window {handle}");

    [Fact]
    public void Create_adds_a_group_in_the_next_slot()
    {
        var reg = new GaggleRegistry();
        var a = reg.Create("A", [M(1), M(2)]);
        var b = reg.Create("B", [M(3)]);

        Assert.Same(a, reg.BySlot(1));
        Assert.Same(b, reg.BySlot(2));
        Assert.Null(reg.BySlot(3));
        Assert.Equal(2, reg.SlotOf(b));
    }

    [Fact]
    public void A_window_moves_to_the_newest_gaggle()
    {
        var reg = new GaggleRegistry();
        var a = reg.Create("A", [M(1), M(2)]);
        var b = reg.Create("B", [M(2), M(3)]);

        Assert.Equal([1L], a.Members.Select(m => m.Handle));
        Assert.Same(b, reg.FindByWindow(2));
    }

    [Fact]
    public void Taking_every_member_drops_the_old_gaggle()
    {
        var reg = new GaggleRegistry();
        reg.Create("A", [M(1)]);
        var b = reg.Create("B", [M(1), M(2)]);

        Assert.Equal([b], reg.Groups);
    }

    [Fact]
    public void Duplicate_members_are_collapsed()
    {
        var reg = new GaggleRegistry();
        var a = reg.Create("A", [M(1), M(1), M(2)]);
        Assert.Equal(2, a.Members.Count);
    }

    [Fact]
    public void Removing_the_last_window_dissolves_the_gaggle()
    {
        var reg = new GaggleRegistry();
        reg.Create("A", [M(1), M(2)]);

        Assert.True(reg.RemoveWindow(1));
        Assert.Single(reg.Groups);
        Assert.True(reg.RemoveWindow(2));
        Assert.Empty(reg.Groups);
        Assert.False(reg.RemoveWindow(2));
    }

    [Fact]
    public void Prune_drops_dead_members_and_empty_groups()
    {
        var reg = new GaggleRegistry();
        reg.Create("A", [M(1), M(2)]);
        reg.Create("B", [M(3)]);

        Assert.True(reg.Prune(m => m.Handle == 1));
        Assert.Equal(["A"], reg.Groups.Select(g => g.Name));
        Assert.False(reg.Prune(m => m.Handle == 1));
    }

    [Fact]
    public void Changed_fires_on_mutations_only()
    {
        var reg = new GaggleRegistry();
        var count = 0;
        reg.Changed += () => count++;

        var a = reg.Create("A", [M(1)]);
        reg.Touch(a);
        reg.Rename(a, "A");          // unchanged
        reg.Rename(a, "Work");
        reg.RemoveWindow(99);        // not a member
        reg.Dissolve(a);

        Assert.Equal(3, count);
    }

    [Fact]
    public void Most_recently_used_follows_touch()
    {
        var time = new ManualTime();
        var reg = new GaggleRegistry(time);
        var a = reg.Create("A", [M(1)]);
        time.Advance(TimeSpan.FromSeconds(1));
        var b = reg.Create("B", [M(2)]);
        Assert.Same(b, reg.MostRecentlyUsed);

        time.Advance(TimeSpan.FromSeconds(1));
        reg.Touch(a);
        Assert.Same(a, reg.MostRecentlyUsed);
    }
}
