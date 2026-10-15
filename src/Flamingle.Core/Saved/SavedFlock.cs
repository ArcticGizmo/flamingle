using System.Text.Json.Serialization;
using Flamingle.Core.Geometry;

namespace Flamingle.Core.Saved;

/// <summary>
/// A flock saved as a recipe (<c>saved-flocks.json</c>): enough about each window to find it again among running
/// apps, or to start its app and put the new window back where it was. Unlike a live <see cref="Grouping.Flock"/>
/// it holds no window handles, so it survives the apps closing and a reboot. Saving the flock again replaces the
/// capture; the designer edits each window's <see cref="SavedWindow.Recipe"/> and the <see cref="Inputs"/> it
/// asks for when it loads.
/// </summary>
/// <param name="Dismissed">Suggested inputs the user said no to (paths), so the designer stops offering them.</param>
public sealed record SavedFlock(
    Guid Id, string Name, DateTimeOffset SavedAt, List<SavedWindow> Windows, List<FlockInput>? Inputs = null,
    List<string>? Dismissed = null)
{
    [JsonIgnore]
    public IReadOnlyList<string> DismissedList => Dismissed ?? [];

    /// <summary>
    /// This flock re-captured as <paramref name="recaptured"/>, keeping what the designer added: its id, inputs and
    /// dismissed suggestions, and each window's edited recipe — carried to the re-captured window of the same app
    /// (matched in order, so a second Code window gets the second Code recipe).
    /// </summary>
    public SavedFlock Recaptured(SavedFlock recaptured)
    {
        var edited = Windows.Where(w => w.Recipe is not null).ToList();
        var windows = recaptured.Windows.Select(w =>
        {
            var match = edited.FirstOrDefault(o => string.Equals(o.AppKey, w.AppKey, StringComparison.OrdinalIgnoreCase));
            if (match is null) return w;
            edited.Remove(match);
            return w.WithRecipe(match.Recipe!);
        }).ToList();
        return recaptured with { Id = Id, Inputs = Inputs, Dismissed = Dismissed, Windows = windows };
    }

    /// <summary>The inputs (none for a flock that starts the same way every time).</summary>
    [JsonIgnore]
    public IReadOnlyList<FlockInput> InputList => Inputs ?? [];

    public FlockInput? Input(string name)
        => InputList.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rename an input and every <c>${name}</c> that uses it — in the windows' recipes and in computed
    /// inputs' commands — so renaming never breaks the flock.</summary>
    public SavedFlock RenameInput(string from, string to)
    {
        if (Input(from) is null || string.Equals(from, to, StringComparison.Ordinal)) return this;
        string Map(string t) => Placeholders.Rename(t, from, to);
        return this with
        {
            Inputs = InputList
                .Select(i => i with
                {
                    Name = string.Equals(i.Name, from, StringComparison.OrdinalIgnoreCase) ? to : i.Name,
                    Default = i.Kind == InputKind.Computed ? Map(i.Default) : i.Default,
                })
                .ToList(),
            Windows = Windows.Select(w => w.WithRecipe(w.EffectiveRecipe().MapTemplates(Map))).ToList(),
        };
    }

    /// <summary>The zone layout most of its zoned windows were in (ties go to the topmost window's), or null if none
    /// was zoned.</summary>
    public string? MainZoneLayout() => Windows
        .OrderBy(w => w.ZIndex)
        .Select(w => w.Zone?.LayoutId)
        .OfType<string>()
        .GroupBy(id => id)
        .OrderByDescending(g => g.Count())
        .Select(g => g.Key)
        .FirstOrDefault();
}

/// <summary>One window of a saved flock.</summary>
/// <param name="ProcessName">e.g. <c>Code</c> — for display.</param>
/// <param name="ExePath">The full image path of the app that owns the window (for a Store app, the app's own
/// process, not <c>ApplicationFrameHost</c>). Null if Flamingle couldn't read it.</param>
/// <param name="Arguments">The process's command line minus the exe. Null if it couldn't be read (e.g. the app runs
/// as administrator); empty if there were none.</param>
/// <param name="AppUserModelId">The window's AppUserModelID, or the package's app id for a Store app. Null when the
/// app doesn't set one.</param>
/// <param name="Packaged">A Store / MSIX app — relaunched by <paramref name="AppUserModelId"/>, not its exe.</param>
/// <param name="ZIndex">0 = the flock's topmost window.</param>
/// <param name="Desktop">The virtual desktop it was on, if known.</param>
/// <param name="Zone">Set if it was snapped into a zone layout.</param>
/// <param name="Elevated">Runs as administrator — relaunching it would prompt.</param>
/// <param name="Recipe">How to open it now, as edited in the designer; null = as captured.</param>
public sealed record SavedWindow(
    string ProcessName,
    string? ExePath,
    string? Arguments,
    string? AppUserModelId,
    bool Packaged,
    string ClassName,
    string Title,
    SavedPlacement Placement,
    int ZIndex,
    Guid? Desktop = null,
    SavedZone? Zone = null,
    bool Elevated = false,
    LaunchRecipe? Recipe = null)
{
    /// <summary>Which app it is, for matching windows across captures: the Store app id, else the exe.</summary>
    [JsonIgnore]
    public string AppKey => (Packaged ? AppUserModelId : ExePath) ?? ProcessName;

    /// <summary>The edited recipe, else the captured one.</summary>
    public LaunchRecipe EffectiveRecipe() => Recipe ?? LaunchRecipe.FromCaptured(this);

    /// <summary>Edited since it was captured ("Changed since it was saved").</summary>
    [JsonIgnore]
    public bool IsChanged => Recipe is not null && Recipe != LaunchRecipe.FromCaptured(this);

    /// <summary>Set the recipe — dropping it when it's just the captured one again.</summary>
    public SavedWindow WithRecipe(LaunchRecipe recipe)
        => this with { Recipe = recipe == LaunchRecipe.FromCaptured(this) ? null : recipe };

    /// <summary>Back to how it was captured ("Undo changes").</summary>
    public SavedWindow AsCaptured() => this with { Recipe = null };

    /// <summary>Where it goes back to, e.g. <c>DISPLAY1 · zones 1+2 of 3 columns</c> or <c>DISPLAY2 · maximised</c>.</summary>
    public string PlaceText()
    {
        var p = Placement;
        var how = Zone is { } z
            ? $"zone{(z.Zones.Count == 1 ? "" : "s")} {string.Join("+", z.Zones.Select(i => i + 1))} of {Zones.ZoneLayout.ById(z.LayoutId).Name}"
            : p.Maximised ? "maximised"
            : $"{p.Restored.Width}×{p.Restored.Height} at {p.Restored.X},{p.Restored.Y}";
        return $"{p.Monitor.TrimStart('\\', '.')} · {how}";
    }
}

/// <summary>Where a window was, in physical pixels, with the monitor it was on so it can be rescaled onto a
/// different set of monitors.</summary>
/// <param name="Monitor">Device name, e.g. <c>\\.\DISPLAY1</c>.</param>
/// <param name="WorkArea">That monitor's work area at the time.</param>
/// <param name="Scale">That monitor's DPI scale (1.0 = 96 dpi).</param>
/// <param name="Restored">The window rect (<c>SetWindowPos</c> terms, invisible borders included) it has when
/// neither minimised nor maximised.</param>
/// <param name="Visible">Its visible frame, when it was on screen and not maximised.</param>
public sealed record SavedPlacement(
    string Monitor,
    PixelRect WorkArea,
    double Scale,
    PixelRect Restored,
    PixelRect? Visible,
    bool Maximised,
    bool Minimised);

/// <summary>The zone(s) a window was snapped into, and the layout they belong to.</summary>
public sealed record SavedZone(string LayoutId, List<int> Zones);

/// <summary>The on-disk list of saved flocks.</summary>
public sealed record SavedFlocks(List<SavedFlock> Flocks)
{
    public static SavedFlocks Empty => new([]);

    /// <summary>Add a saved flock, replacing any with the same name — it keeps that one's place in the list and what
    /// the designer added to it (<see cref="SavedFlock.Recaptured"/>). <paramref name="replaced"/> says which
    /// happened.</summary>
    public SavedFlocks Save(SavedFlock flock, out bool replaced)
    {
        var list = Flocks.ToList();
        var i = list.FindIndex(f => string.Equals(f.Name, flock.Name, StringComparison.OrdinalIgnoreCase));
        replaced = i >= 0;
        if (replaced) list[i] = list[i].Recaptured(flock);
        else list.Add(flock);
        return new SavedFlocks(list);
    }

    /// <summary>Store an edit from the designer: replace the saved flock with the same id, or add it if it's new.</summary>
    public SavedFlocks Update(SavedFlock flock)
        => Flocks.Any(f => f.Id == flock.Id)
            ? new(Flocks.Select(f => f.Id == flock.Id ? flock : f).ToList())
            : new([.. Flocks, flock]);

    /// <summary><paramref name="name"/>, or "name 2", "name 3"… if a saved flock already has it.</summary>
    public string FreeName(string name)
    {
        var candidate = name;
        for (var n = 2; Flocks.Any(f => string.Equals(f.Name, candidate, StringComparison.OrdinalIgnoreCase)); n++)
            candidate = $"{name} {n}";
        return candidate;
    }

    public SavedFlocks Remove(Guid id) => new(Flocks.Where(f => f.Id != id).ToList());
}
