using Gaggle.Core.Grouping;
using Gaggle.Core.Hotkeys;
using Gaggle.Core.Persistence;
using Gaggle.Core.Settings;

namespace Gaggle.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gaggle-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Missing_file_loads_the_fallback()
    {
        var file = new JsonFile<AppSettings>(Path.Combine(_dir, "settings.json"));
        Assert.Same(AppSettings.Default, file.Load(AppSettings.Default));
    }

    [Fact]
    public void Corrupt_file_loads_the_fallback()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        Assert.Same(AppSettings.Default, new JsonFile<AppSettings>(path).Load(AppSettings.Default));
    }

    [Fact]
    public void Settings_round_trip()
    {
        var file = new JsonFile<AppSettings>(Path.Combine(_dir, "settings.json"));
        var custom = (AppSettings.Default with { ToggleHotkey = "Ctrl+Alt+F9", LinkedMinimise = false })
            .WithLayout(@"\\.\DISPLAY2", "grid-2x2");

        Assert.True(file.Save(custom));
        var loaded = file.Load(AppSettings.Default);
        Assert.Equal("Ctrl+Alt+F9", loaded.ToggleHotkey);
        Assert.False(loaded.LinkedMinimise);
        Assert.Equal("grid-2x2", loaded.LayoutIdFor(@"\\.\DISPLAY2"));
        Assert.Equal("columns-2", loaded.LayoutIdFor(@"\\.\DISPLAY1"));
    }

    [Fact]
    public void Zone_monitor_survives_a_round_trip()
    {
        var reg = new GaggleRegistry();
        reg.Create("Zones", [new GaggleMember(1, 10, "Code", "a")], zoneMonitor: @"\\.\DISPLAY1");

        var file = new JsonFile<GaggleSnapshot>(Path.Combine(_dir, "gaggles.json"));
        file.Save(GaggleSnapshot.From(reg));

        var loaded = new GaggleRegistry();
        loaded.Load(file.Load(GaggleSnapshot.Empty).ToGroups(_ => true));
        Assert.Equal(@"\\.\DISPLAY1", loaded.Groups.Single().ZoneMonitor);
    }

    [Fact]
    public void Gaggles_round_trip_and_drop_dead_members()
    {
        var reg = new GaggleRegistry();
        reg.Create("Work", [new GaggleMember(1, 10, "Code", "a"), new GaggleMember(2, 20, "msedge", "b")]);
        reg.Create("Gone", [new GaggleMember(3, 30, "x", "c")]);

        var file = new JsonFile<GaggleSnapshot>(Path.Combine(_dir, "gaggles.json"));
        Assert.True(file.Save(GaggleSnapshot.From(reg)));

        var loaded = new GaggleRegistry();
        loaded.Load(file.Load(GaggleSnapshot.Empty).ToGroups(m => m.Handle != 3));

        var work = Assert.Single(loaded.Groups);
        Assert.Equal("Work", work.Name);
        Assert.Equal(reg.Groups[0].Id, work.Id);
        Assert.Equal([1L, 2L], work.Members.Select(m => m.Handle));
    }
}

public class HotkeyTests
{
    [Theory]
    [InlineData("Alt+Shift+G", "Alt+Shift+G")]
    [InlineData("shift + alt + g", "Alt+Shift+G")]
    [InlineData("Ctrl+Win+F12", "Ctrl+Win+F12")]
    [InlineData("Alt+Shift+3", "Alt+Shift+3")]
    public void Parses_and_normalises(string text, string expected)
        => Assert.Equal(expected, Hotkey.Parse(text).ToString());

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Alt+Shift")]
    [InlineData("Alt+G+H")]
    [InlineData("Alt+Space")]
    public void TryParse_rejects_garbage(string? text) => Assert.Null(Hotkey.TryParse(text));

    [Fact]
    public void Maps_to_win32()
    {
        var hk = Hotkey.Parse("Alt+Shift+F1");
        Assert.Equal(0x0001u | 0x0004u | 0x4000u, hk.ToWin32Modifiers());
        Assert.Equal(0x70u, hk.ToVirtualKey());
    }
}
