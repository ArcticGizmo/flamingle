using System.Text.Json;
using System.Text.Json.Nodes;

namespace Flamingle.Core.Persistence;

/// <summary>
/// Flamingle used to be called Gaggle. On first run after the rename this moves the old <c>%APPDATA%\Gaggle</c>
/// folder (or the dev one) to its new name, so existing settings and flocks carry over: <c>gaggles.json</c>
/// becomes <c>flocks.json</c>, and the two settings keys that had "Gaggle" in them are renamed. Forgiving like
/// <see cref="JsonFile{T}"/> — a failed migration returns false rather than stopping the app starting.
/// </summary>
public static class LegacyData
{
    private static readonly (string From, string To)[] SettingsKeys =
    [
        ("NewGaggleHotkey", "NewFlockHotkey"),
        ("ZonesFormGaggle", "ZonesFormFlock"),
    ];

    /// <summary>True if a legacy folder was found and moved.</summary>
    public static bool Migrate(string appDataRoot, bool dev)
    {
        try
        {
            var legacy = Path.Combine(appDataRoot, AppProfile.LegacyFolderName(dev));
            var current = Path.Combine(appDataRoot, AppProfile.FolderName(dev));
            if (Directory.Exists(current) || !Directory.Exists(legacy)) return false;

            Directory.Move(legacy, current);

            var oldFlocks = Path.Combine(current, "gaggles.json");
            var flocks = Path.Combine(current, "flocks.json");
            if (File.Exists(oldFlocks) && !File.Exists(flocks))
                File.Move(oldFlocks, flocks);

            RenameSettingsKeys(Path.Combine(current, "settings.json"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RenameSettingsKeys(string path)
    {
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject settings) return;

        var changed = false;
        foreach (var (from, to) in SettingsKeys)
        {
            var old = settings.Select(p => p.Key).FirstOrDefault(k => k.Equals(from, StringComparison.OrdinalIgnoreCase));
            if (old is null || settings.Any(p => p.Key.Equals(to, StringComparison.OrdinalIgnoreCase))) continue;

            var value = settings[old];
            settings.Remove(old);
            settings[to] = value;
            changed = true;
        }

        if (changed)
            File.WriteAllText(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
