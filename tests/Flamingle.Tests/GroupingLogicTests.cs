using Flamingle.Core.Grouping;

namespace Flamingle.Tests;

public class ToggleDecisionTests
{
    [Fact]
    public void All_minimised_restores() =>
        Assert.Equal(FlockAction.Restore, ToggleDecision.Decide([true, true], focusInFlock: false));

    [Fact]
    public void Partly_minimised_restores_even_when_focused() =>
        Assert.Equal(FlockAction.Restore, ToggleDecision.Decide([true, false], focusInFlock: true));

    [Fact]
    public void Up_and_focused_minimises() =>
        Assert.Equal(FlockAction.Minimise, ToggleDecision.Decide([false, false], focusInFlock: true));

    [Fact]
    public void Up_but_behind_another_app_is_raised_not_minimised() =>
        Assert.Equal(FlockAction.Restore, ToggleDecision.Decide([false, false], focusInFlock: false));
}

public class ZOrderTests
{
    [Fact]
    public void Sorts_by_z_order_with_unknowns_last()
        => Assert.Equal([3L, 1L, 9L, 8L], ZOrder.Sort([9, 1, 8, 3], [5, 3, 7, 1]));

    [Fact]
    public void Bring_to_front_keeps_the_rest_in_order()
        => Assert.Equal([2L, 1L, 3L], ZOrder.BringToFront([1, 2, 3], 2));

    [Fact]
    public void Bring_to_front_ignores_non_members()
        => Assert.Equal([1L, 2L], ZOrder.BringToFront([1, 2], 7));
}

public class EchoSuppressorTests
{
    [Fact]
    public void Suppression_expires()
    {
        var time = new ManualTime();
        var echo = new EchoSuppressor(time);

        echo.Suppress(1, TimeSpan.FromMilliseconds(500));
        Assert.True(echo.IsSuppressed(1));
        Assert.False(echo.IsSuppressed(2));

        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.True(echo.IsSuppressed(1));

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(echo.IsSuppressed(1));
    }

    [Fact]
    public void Suppressing_again_extends_the_window()
    {
        var time = new ManualTime();
        var echo = new EchoSuppressor(time);

        echo.Suppress(1, TimeSpan.FromMilliseconds(500));
        time.Advance(TimeSpan.FromMilliseconds(400));
        echo.Suppress(1, TimeSpan.FromMilliseconds(500));
        time.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(echo.IsSuppressed(1));
    }
}

public class FlockNamingTests
{
    [Fact]
    public void Joins_distinct_apps() =>
        Assert.Equal("Code + msedge", FlockNaming.Suggest(["Code", "msedge", "code"], 1));

    [Fact]
    public void Caps_at_three_apps() =>
        Assert.Equal("a + b + c +2", FlockNaming.Suggest(["a", "b", "c", "d", "e"], 1));

    [Fact]
    public void Falls_back_to_a_number() =>
        Assert.Equal("Flock 4", FlockNaming.Suggest(["", " "], 4));
}
