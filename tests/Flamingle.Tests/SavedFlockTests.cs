using Flamingle.Core.Geometry;
using Flamingle.Core.Persistence;
using Flamingle.Core.Saved;

namespace Flamingle.Tests;

public sealed class SavedFlockTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flamingle-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SavedWindow Window(string process, int z = 0, SavedZone? zone = null) => new(
        process, $@"C:\Apps\{process}.exe", "--flag \"a b\"", null, Packaged: false, "Chrome_WidgetWin_1", $"{process} title",
        new SavedPlacement(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1400), 1.5,
            new PixelRect(-7, 0, 1294, 1407), new PixelRect(0, 0, 1280, 1400), Maximised: false, Minimised: false),
        z, Guid.NewGuid(), zone);

    private static SavedFlock Flock(string name, params SavedWindow[] windows)
        => new(Guid.NewGuid(), name, DateTimeOffset.UnixEpoch, [.. windows]);

    [Fact]
    public void Saving_a_new_name_appends()
    {
        var saved = SavedFlocks.Empty.Save(Flock("Work", Window("Code")), out var replaced)
            .Save(Flock("Play", Window("Spotify")), out var replaced2);

        Assert.False(replaced);
        Assert.False(replaced2);
        Assert.Equal(["Work", "Play"], saved.Flocks.Select(f => f.Name));
    }

    [Fact]
    public void Saving_the_same_name_replaces_in_place_and_keeps_the_id()
    {
        var first = Flock("Work", Window("Code"));
        var saved = SavedFlocks.Empty.Save(first, out _).Save(Flock("Play"), out _);

        saved = saved.Save(Flock("work", Window("Code"), Window("msedge", 1)), out var replaced);

        Assert.True(replaced);
        Assert.Equal(2, saved.Flocks.Count);
        Assert.Equal(first.Id, saved.Flocks[0].Id);
        Assert.Equal(2, saved.Flocks[0].Windows.Count);
    }

    [Fact]
    public void Remove_drops_by_id()
    {
        var keep = Flock("Keep");
        var drop = Flock("Drop");
        var saved = SavedFlocks.Empty.Save(keep, out _).Save(drop, out _).Remove(drop.Id);
        Assert.Equal([keep.Id], saved.Flocks.Select(f => f.Id));
    }

    [Fact]
    public void Round_trips_through_json()
    {
        var flock = Flock("Work", Window("Code", zone: new SavedZone("columns-3", [0, 1])), Window("msedge", 1));
        var file = new JsonFile<SavedFlocks>(Path.Combine(_dir, "saved-flocks.json"));
        Assert.True(file.Save(SavedFlocks.Empty.Save(flock, out _)));

        var loaded = Assert.Single(file.Load(SavedFlocks.Empty).Flocks);
        Assert.Equal(flock.Id, loaded.Id);
        Assert.Equal(flock.Windows[0] with { Zone = null }, loaded.Windows[0] with { Zone = null });
        Assert.Equal(["columns-3"], [loaded.Windows[0].Zone!.LayoutId]);
        Assert.Equal([0, 1], loaded.Windows[0].Zone!.Zones);
        Assert.Equal(flock.Windows[1], loaded.Windows[1]);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --a b", "--a b")]
    [InlineData(@"""C:\Program Files\App\app.exe""", "")]
    [InlineData(@"C:\Tools\app.exe --a ""x y""", @"--a ""x y""")]
    [InlineData(@"app.exe", "")]
    [InlineData("  app.exe\t  -n  ", "-n  ")]
    [InlineData(@"""unterminated quote", "")]
    [InlineData("", "")]
    public void Arguments_drop_the_program(string commandLine, string expected)
        => Assert.Equal(expected, CommandLine.Arguments(commandLine));
}
