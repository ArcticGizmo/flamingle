using Flamingle.Core.Persistence;
using Flamingle.Core.Settings;

namespace Flamingle.Tests;

public sealed class LegacyDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flamingle-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Moves_the_gaggle_folder_and_renames_its_files_and_settings_keys()
    {
        var legacy = Directory.CreateDirectory(Path.Combine(_root, "Gaggle")).FullName;
        File.WriteAllText(Path.Combine(legacy, "gaggles.json"), """{ "Groups": [] }""");
        File.WriteAllText(Path.Combine(legacy, "settings.json"),
            """{ "NewGaggleHotkey": "Ctrl+Alt+N", "zonesFormGaggle": false, "ToggleHotkey": "Ctrl+Alt+M" }""");

        Assert.True(LegacyData.Migrate(_root, dev: false));

        var current = Path.Combine(_root, "Flamingle");
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(current, "flocks.json")));
        Assert.False(File.Exists(Path.Combine(current, "gaggles.json")));

        var settings = new JsonFile<AppSettings>(Path.Combine(current, "settings.json")).Load(AppSettings.Default);
        Assert.Equal("Ctrl+Alt+N", settings.NewFlockHotkey);
        Assert.False(settings.ZonesFormFlock);
        Assert.Equal("Ctrl+Alt+M", settings.ToggleHotkey);
    }

    [Fact]
    public void Dev_uses_the_dev_folders()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Gaggle (Dev)"));
        Directory.CreateDirectory(Path.Combine(_root, "Gaggle"));

        Assert.True(LegacyData.Migrate(_root, dev: true));

        Assert.True(Directory.Exists(Path.Combine(_root, "Flamingle (Dev)")));
        Assert.True(Directory.Exists(Path.Combine(_root, "Gaggle")));
    }

    [Fact]
    public void Does_nothing_when_the_new_folder_already_exists()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Gaggle"));
        Directory.CreateDirectory(Path.Combine(_root, "Flamingle"));

        Assert.False(LegacyData.Migrate(_root, dev: false));
        Assert.True(Directory.Exists(Path.Combine(_root, "Gaggle")));
    }

    [Fact]
    public void Does_nothing_when_there_is_no_legacy_folder()
    {
        Directory.CreateDirectory(_root);

        Assert.False(LegacyData.Migrate(_root, dev: false));
        Assert.False(Directory.Exists(Path.Combine(_root, "Flamingle")));
    }
}
