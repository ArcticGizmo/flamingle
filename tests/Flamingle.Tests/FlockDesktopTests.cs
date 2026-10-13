using Flamingle.Core.Grouping;

namespace Flamingle.Tests;

public class FlockDesktopTests
{
    private static readonly Guid A = new("aaaaaaaa-0000-0000-0000-000000000000");
    private static readonly Guid B = new("bbbbbbbb-0000-0000-0000-000000000000");

    [Fact]
    public void Nothing_known_gives_null() =>
        Assert.Null(FlockDesktop.Choose([Guid.Empty, Guid.Empty], focusedDesktop: null));

    [Fact]
    public void Majority_wins_over_focus() =>
        Assert.Equal(A, FlockDesktop.Choose([A, B, A], focusedDesktop: B));

    [Fact]
    public void Tie_goes_to_the_focused_members_desktop() =>
        Assert.Equal(B, FlockDesktop.Choose([A, B], focusedDesktop: B));

    [Fact]
    public void Tie_without_focus_goes_to_the_earliest_member() =>
        Assert.Equal(B, FlockDesktop.Choose([Guid.Empty, B, A], focusedDesktop: null));

    [Fact]
    public void Unknown_desktops_are_ignored() =>
        Assert.Equal(A, FlockDesktop.Choose([Guid.Empty, Guid.Empty, A], focusedDesktop: null));
}
