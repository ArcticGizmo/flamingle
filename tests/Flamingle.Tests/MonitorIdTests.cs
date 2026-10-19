using Flamingle.Core.Geometry;

namespace Flamingle.Tests;

public class MonitorIdTests
{
    [Fact]
    public void Interface_path_becomes_the_pnp_instance_id()
    {
        var id = MonitorIds.FromInterfacePath(@"\\?\DISPLAY#ACR03E3#8&3157d11d&0&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");
        Assert.Equal(@"DISPLAY\ACR03E3\8&3157D11D&0&UID256", id);
    }

    [Fact]
    public void Two_identical_monitors_get_different_ids()
    {
        var a = MonitorIds.FromInterfacePath(@"\\?\DISPLAY#ACR03E3#8&3157d11d&0&UID256#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");
        var b = MonitorIds.FromInterfacePath(@"\\?\DISPLAY#ACR03E3#8&3157d11d&0&UID257#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}");
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\.\DISPLAY1")]
    [InlineData(@"\\?\DISPLAY#ACR03E3#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")]
    public void Anything_else_has_no_id(string? path) => Assert.Null(MonitorIds.FromInterfacePath(path));

    [Theory]
    [InlineData(@"\\.\DISPLAY1", "DISPLAY1")]
    [InlineData(@"\\.\DISPLAY1781", "DISPLAY1781")]
    [InlineData(@"DISPLAY\ACR03E3\8&3157D11D&0&UID256", "ACR03E3")]
    public void Short_names(string deviceName, string expected) => Assert.Equal(expected, MonitorIds.ShortName(deviceName));

    [Fact]
    public void A_monitor_shows_its_gdi_name_but_is_keyed_by_its_id()
    {
        var m = new MonitorInfo(1, @"DISPLAY\ACR03E3\8&3157D11D&0&UID256", default, default, 1.0, false, @"\\.\DISPLAY1781");
        Assert.Equal("DISPLAY1781", m.ShortName);
    }
}
