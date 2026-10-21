using System.Text.Json;
using System.Text.Json.Serialization;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Persistence;
using Flamingle.Core.Saved;
using Flamingle.Core.Settings;
using Flamingle.Core.Zones;

namespace Flamingle.Tests;

/// <summary>
/// The release build is trimmed, so <see cref="JsonFile{T}"/> uses source-generated JSON. These pin it to the format
/// the reflection serializer wrote before that, so files from older versions keep loading (and nothing is lost on
/// the next save).
/// </summary>
public sealed class JsonFormatTests
{
    private static readonly JsonSerializerOptions Reflection = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private static void SameAsReflection<T>(T value) where T : class
    {
        var reflection = JsonSerializer.Serialize(value, Reflection);
        Assert.Equal(reflection, JsonFile<T>.ToJson(value));

        var path = Path.Combine(Path.GetTempPath(), "flamingle-tests-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, reflection);
            var loaded = new JsonFile<T>(path).Load(null!);
            Assert.Equal(reflection, JsonSerializer.Serialize(loaded, Reflection));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Settings() => SameAsReflection(
        (AppSettings.Default with { LinkedMinimise = false, LastSeenVersion = "0.1.2" }).WithLayout(@"\\.\DISPLAY2", "grid-2x2"));

    [Fact]
    public void Flocks()
    {
        var reg = new FlockRegistry();
        reg.Create("Zones", [new FlockMember(1, 10, "Code", "a")], zoneMonitor: @"\\.\DISPLAY1", zoneDesktop: Guid.NewGuid());
        reg.Create("Work", [new FlockMember(2, 20, "msedge", "b")]);
        SameAsReflection(FlockSnapshot.From(reg));
    }

    [Fact]
    public void Zones() => SameAsReflection(new List<ZoneContainerState>
    {
        new(@"\\.\DISPLAY1", "columns-3", new PixelRect(0, 0, 1920, 1040),
            [new ZoneSlot(1, 10, [0, 1], new PixelRect(100, 100, 800, 600))],
            [new ZoneFraction(0, 0, 0.5, 1), new ZoneFraction(0.5, 0, 0.5, 1)], new PixelRect(0, 0, 1920, 1040)),
    });

    [Fact]
    public void Saved_flocks()
    {
        var area = new PixelRect(0, 0, 1920, 1040);
        var window = new SavedWindow("Code", @"C:\Apps\Code.exe", "-n", null, false, "W", "t",
            new SavedPlacement(@"\\.\DISPLAY1", area, 1.5, new PixelRect(10, 10, 800, 600), null, false, true),
            0, Guid.NewGuid(), new SavedZone("columns-2", [1]), Elevated: true);
        var flock = new SavedFlock(Guid.NewGuid(), "Work", DateTimeOffset.UnixEpoch,
            [window.WithRecipe(new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", "${worktree}",
                StartIn: "${worktree}", IfOpen: IfAlreadyOpen.StartAnother, WaitSeconds: 30))],
            [new FlockInput("worktree", "Worktree", InputKind.Folder, @"C:\src", RememberRecent: true, Recent: [@"C:\a"])],
            [@"C:\b"]);

        SameAsReflection(new SavedFlocks([flock]));
        SameAsReflection(flock);
    }
}
