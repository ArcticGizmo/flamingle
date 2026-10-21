using System.Text.Json.Serialization;
using Flamingle.Core.Saved;
using Flamingle.Core.Settings;
using Flamingle.Core.Zones;

namespace Flamingle.Core.Persistence;

/// <summary>
/// Source-generated JSON for everything <see cref="JsonFile{T}"/> stores. The release build is trimmed, which turns
/// off reflection-based System.Text.Json, so a type missing here can't be loaded or saved — add it when adding a
/// new <c>JsonFile&lt;T&gt;</c>. The options match the reflection ones the files were first written with.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(FlockSnapshot))]
[JsonSerializable(typeof(List<ZoneContainerState>))]
[JsonSerializable(typeof(SavedFlocks))]
[JsonSerializable(typeof(SavedFlock))]
internal sealed partial class FlamingleJson : JsonSerializerContext;
