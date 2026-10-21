using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Flamingle.Core.Persistence;

/// <summary>
/// A forgiving JSON file: a missing, unreadable or malformed file loads as the fallback rather than throwing
/// (state should never stop the app starting), and a failed save returns false. The path is injectable so
/// it's headless-testable. <typeparamref name="T"/> must be listed in <see cref="FlamingleJson"/>.
/// </summary>
public sealed class JsonFile<T>(string path) where T : class
{
    // Resolved up front (outside Load/Save's catch-alls) so an unlisted type fails loudly, not as silent data loss.
    private static JsonTypeInfo<T> Json => field ??= FlamingleJson.Default.GetTypeInfo(typeof(T)) as JsonTypeInfo<T>
        ?? throw new NotSupportedException($"Add [JsonSerializable(typeof({typeof(T).Name}))] to {nameof(FlamingleJson)}.");

    private readonly JsonTypeInfo<T> _json = Json;

    public string Path { get; } = path;

    /// <summary>A value as this file would store it (e.g. to show it).</summary>
    public static string ToJson(T value) => JsonSerializer.Serialize(value, Json);

    public T Load(T fallback)
    {
        try
        {
            if (!File.Exists(Path)) return fallback;
            return JsonSerializer.Deserialize(File.ReadAllText(Path), _json) ?? fallback;
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
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, _json));
            File.Move(tmp, Path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
