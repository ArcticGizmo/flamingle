using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Flamingle.Core.Saved;

/// <summary>How a saved window is opened when its flock loads.</summary>
public enum LaunchKind
{
    /// <summary>Start an exe with arguments.</summary>
    Program,

    /// <summary>Start a Store / MSIX app by its AppUserModelID.</summary>
    StoreApp,

    /// <summary>Never start it — use a matching window that's already open.</summary>
    AlreadyOpen,

    /// <summary>Leave it out of the load.</summary>
    Skip,
}

/// <summary>What to do when a window matching a saved one is already open.</summary>
public enum IfAlreadyOpen
{
    UseIt,
    StartAnother,
    Ask,
}

/// <summary>
/// How to open a saved window <em>now</em> — the captured launch, possibly edited in the designer. The target,
/// arguments, start-in folder and title to recognise it by may all hold <c>${name}</c> placeholders (see
/// <see cref="Placeholders"/>).
/// </summary>
/// <param name="Target">The exe path (<see cref="LaunchKind.Program"/>) or AppUserModelID (<see cref="LaunchKind.StoreApp"/>).</param>
/// <param name="StartIn">The working directory; null = the exe's own folder.</param>
/// <param name="TitleContains">Recognise its window by a title containing this; null = any window of the app.</param>
/// <param name="WaitSeconds">How long to wait for its window to appear after starting it.</param>
public sealed record LaunchRecipe(
    LaunchKind Kind,
    string Target,
    string Arguments,
    string? StartIn = null,
    string? TitleContains = null,
    IfAlreadyOpen IfOpen = IfAlreadyOpen.UseIt,
    int WaitSeconds = LaunchRecipe.DefaultWaitSeconds)
{
    public const int DefaultWaitSeconds = 20;
    public const int MaxWaitSeconds = 300;

    /// <summary>The recipe a window gets from what was captured.</summary>
    public static LaunchRecipe FromCaptured(SavedWindow w) => new(
        w.Packaged ? LaunchKind.StoreApp : w.ExePath is null ? LaunchKind.AlreadyOpen : LaunchKind.Program,
        (w.Packaged ? w.AppUserModelId : w.ExePath) ?? "",
        w.Arguments ?? "");

    [JsonIgnore]
    public bool Launches => Kind is LaunchKind.Program or LaunchKind.StoreApp;

    /// <summary>Every field that may hold placeholders.</summary>
    public IEnumerable<string> Templates()
    {
        yield return Target;
        yield return Arguments;
        if (StartIn is not null) yield return StartIn;
        if (TitleContains is not null) yield return TitleContains;
    }

    /// <summary>The same recipe with every template rewritten.</summary>
    public LaunchRecipe MapTemplates(Func<string, string> map) => this with
    {
        Target = map(Target),
        Arguments = map(Arguments),
        StartIn = StartIn is null ? null : map(StartIn),
        TitleContains = TitleContains is null ? null : map(TitleContains),
    };
}

/// <summary>What kind of value an input takes — decides how the load prompt asks for it.</summary>
public enum InputKind
{
    Text,
    Folder,
    File,
    Number,

    /// <summary>One of a fixed list: <see cref="FlockInput.Default"/> holds the choices, comma-separated.</summary>
    Choice,

    /// <summary>Not asked for: <see cref="FlockInput.Default"/> is a command run at load, whose output is the value.</summary>
    Computed,
}

/// <summary>
/// A value a saved flock asks for when it loads, used in its windows' recipes as <c>${Name}</c>.
/// </summary>
/// <param name="Name">What recipes call it — letters, digits and <c>_</c>, not starting with a digit.</param>
/// <param name="Label">What the load prompt calls it.</param>
/// <param name="Default">The default value; the choices for <see cref="InputKind.Choice"/>; the command for
/// <see cref="InputKind.Computed"/>.</param>
/// <param name="Recent">Values used in earlier loads, newest first (kept when <paramref name="RememberRecent"/>).</param>
public sealed record FlockInput(
    string Name,
    string Label,
    InputKind Kind,
    string Default = "",
    bool RememberRecent = false,
    List<string>? Recent = null)
{
    public const int RecentKept = 5;

    private static readonly Regex NamePattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string name) => NamePattern.IsMatch(name);

    /// <summary>For a <see cref="InputKind.Choice"/> input, its choices.</summary>
    public IReadOnlyList<string> Choices()
        => Default.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The value used when the user gives none.</summary>
    public string DefaultValue() => Kind == InputKind.Choice ? Choices().FirstOrDefault() ?? "" : Default;

    /// <summary>Remember <paramref name="value"/> as the most recent (when this input remembers values).</summary>
    public FlockInput WithRecent(string value)
    {
        if (!RememberRecent || string.IsNullOrWhiteSpace(value)) return this;
        var recent = new List<string> { value };
        recent.AddRange((Recent ?? []).Where(r => !string.Equals(r, value, StringComparison.OrdinalIgnoreCase)));
        return this with { Recent = recent.Take(RecentKept).ToList() };
    }
}
