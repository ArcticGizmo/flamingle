namespace Flamingle.Core.Saved;

/// <summary>One saved window with its recipe filled in for this load.</summary>
/// <param name="Target">The exe path or AppUserModelID, filled in.</param>
/// <param name="StartIn">The working directory, filled in; null = the exe's own folder.</param>
/// <param name="TitleContains">The title to recognise its window by, filled in; null = any window of the app.</param>
/// <param name="Unknown">Names in its recipe that no input defines (left as written).</param>
public sealed record PlannedWindow(
    SavedWindow Window,
    LaunchRecipe Recipe,
    string Target,
    string Arguments,
    string? StartIn,
    string? TitleContains,
    IReadOnlyList<string> Unknown)
{
    /// <summary>What loading will do with this window, in a sentence — the Test load list.</summary>
    public string Describe()
    {
        var app = Window.ProcessName;
        var titled = string.IsNullOrEmpty(TitleContains) ? "" : $" titled “{TitleContains}”";
        var place = Window.PlaceText();
        return Recipe.Kind switch
        {
            LaunchKind.Skip => $"Skips {app}.",
            LaunchKind.AlreadyOpen => $"Waits for an open {app} window{titled}, then puts it on {place}.",
            _ => Recipe.IfOpen switch
            {
                IfAlreadyOpen.UseIt => $"Uses an open {app} window{titled}, or starts one, on {place}.",
                IfAlreadyOpen.Ask => $"Asks whether to use an open {app} window{titled}, on {place}.",
                _ => $"Starts a new {app} window{titled}, on {place}.",
            },
        };
    }
}

/// <summary>
/// A saved flock filled in with one load's input values: per window, what to start (or find) and how to recognise
/// it. Pure — the designer's Test load and the real relaunch both read it.
/// </summary>
public sealed record LoadPlan(IReadOnlyList<PlannedWindow> Windows)
{
    /// <summary>Every name used that no input defines.</summary>
    public IReadOnlyList<string> Unknown
        => Windows.SelectMany(w => w.Unknown).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Fill in every window's recipe (topmost window first). <paramref name="environment"/> reads
    /// <c>${env:…}</c> (default: the process's environment).</summary>
    public static LoadPlan Build(SavedFlock flock, IReadOnlyDictionary<string, string> values,
        Func<string, string?>? environment = null)
    {
        var windows = flock.Windows
            .OrderBy(w => w.ZIndex)
            .Select(w =>
            {
                var recipe = w.EffectiveRecipe();
                var unknown = new List<string>();
                string Fill(string template)
                {
                    var e = Placeholders.Expand(template, values, environment);
                    unknown.AddRange(e.Unknown);
                    return e.Text;
                }
                string? FillOptional(string? template)
                    => string.IsNullOrWhiteSpace(template) ? null : Fill(template);

                return new PlannedWindow(
                    w, recipe, Fill(recipe.Target), Fill(recipe.Arguments), FillOptional(recipe.StartIn),
                    FillOptional(recipe.TitleContains), unknown.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            })
            .ToList();
        return new LoadPlan(windows);
    }
}
