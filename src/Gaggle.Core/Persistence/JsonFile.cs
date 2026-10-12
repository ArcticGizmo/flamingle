using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gaggle.Core.Persistence;

/// <summary>
/// A forgiving JSON file: a missing, unreadable or malformed file loads as the fallback rather than throwing
/// (state should never stop the app starting), and a failed save returns false. The path is injectable so
/// it's headless-testable.
/// </summary>
public sealed class JsonFile<T>(string path) where T : class
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public string Path { get; } = path;

    public T Load(T fallback)
    {
        try
        {
            if (!File.Exists(Path)) return fallback;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(Path), Json) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public bool Save(T value)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // Write-then-swap so a crash mid-write can't leave a truncated file behind.
            var tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Json));
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
