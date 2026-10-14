using Flamingle.Core.Taskbar;

namespace Flamingle.Tests;

public class FlockBadgeTests
{
    private const string One = @"\\.\DISPLAY1";
    private const string Two = @"\\.\DISPLAY2";

    [Fact]
    public void A_zone_flock_is_on_its_own_monitor() =>
        Assert.Equal(Two, FlockBadge.MonitorOf(Two, [One, One]));

    [Fact]
    public void Otherwise_the_monitor_most_windows_are_on() =>
        Assert.Equal(Two, FlockBadge.MonitorOf(null, [One, Two, Two]));

    [Fact]
    public void A_tie_goes_to_the_earliest_member() =>
        Assert.Equal(Two, FlockBadge.MonitorOf(null, [null, Two, One]));

    [Fact]
    public void No_known_monitors_gives_null() =>
        Assert.Null(FlockBadge.MonitorOf(null, [null, null]));

    [Theory]
    [InlineData(@"\\.\DISPLAY1", "1")]
    [InlineData(@"\\.\DISPLAY12", "12")]
    [InlineData(@"\\.\DISPLAY", null)]
    [InlineData(null, null)]
    public void Number_is_the_display_number(string? device, string? expected) =>
        Assert.Equal(expected, FlockBadge.Number(device));
}
