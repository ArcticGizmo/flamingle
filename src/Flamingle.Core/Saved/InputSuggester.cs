using System.Text.RegularExpressions;

namespace Flamingle.Core.Saved;

/// <summary>A folder path the windows of a saved flock share — worth asking for when the flock loads.</summary>
/// <param name="Windows">How many windows use it.</param>
public sealed record InputSuggestion(string Path, int Windows);

/// <summary>
/// Spots values in a captured flock that would make good inputs (so far: folder paths in arguments and start-in
/// folders, e.g. the project every window was opened on) and turns one into an input in a single step.
/// </summary>
public static class InputSuggester
{
    // A drive-rooted path, up to a quote or whitespace. Paths with spaces are only caught up to the space.
    private static readonly Regex DrivePath = new(@"(?<![A-Za-z0-9])[A-Za-z]:\\[^""\s]*", RegexOptions.CultureInvariant);

    /// <summary>The path used by the most windows (ties: the first seen), ignoring <paramref name="dismissed"/>
    /// ones and anything already a placeholder. Null when there's nothing to suggest.</summary>
    public static InputSuggestion? Suggest(SavedFlock flock, IEnumerable<string> dismissed)
    {
        var skip = dismissed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var w in flock.Windows)
        {
            var recipe = w.EffectiveRecipe();
            if (recipe.Kind == LaunchKind.Skip) continue;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in new[] { recipe.Arguments, recipe.StartIn ?? "" })
            {
                foreach (Match m in DrivePath.Matches(text))
                {
                    var path = m.Value.TrimEnd('\\');
                    if (path.Length <= 2 || skip.Contains(path)) continue;   // a bare drive isn't a project
                    seen.Add(path);
                }
            }
            foreach (var path in seen)
            {
                if (counts.TryAdd(path, 1)) order.Add(path);
                else counts[path]++;
            }
        }

        var best = order.OrderByDescending(p => counts[p]).FirstOrDefault();
        return best is null ? null : new InputSuggestion(best, counts[best]);
    }

    /// <summary>
    /// Make <paramref name="path"/> an input: a Folder input defaulting to it (remembering recent values), with every
    /// use of the path in the windows' arguments and start-in folders replaced by <c>${name}</c>. Only whole uses are
    /// replaced — <c>C:\src\app</c> inside <c>C:\src\app-two</c> is left alone; <c>C:\src\app\bin</c> becomes
    /// <c>${name}\bin</c>.
    /// </summary>
    /// <param name="name">The input's name; made unique if taken.</param>
    public static SavedFlock MakeInput(SavedFlock flock, string path, string name = "folder", string label = "Project folder")
    {
        name = FreeName(flock, name);
        var use = new Regex(Regex.Escape(path.TrimEnd('\\')) + @"(?=$|[""\s\\/])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // An evaluator, not a replacement pattern — "${name}" means a regex group in a pattern.
        string Swap(string t) => use.Replace(t, _ => "${" + name + "}");

        return flock with
        {
            Inputs = [.. flock.InputList, new FlockInput(name, label, InputKind.Folder, path.TrimEnd('\\'), RememberRecent: true)],
            Windows = flock.Windows
                .Select(w =>
                {
                    var r = w.EffectiveRecipe();
                    return w.WithRecipe(r with { Arguments = Swap(r.Arguments), StartIn = r.StartIn is null ? null : Swap(r.StartIn) });
                })
                .ToList(),
        };
    }

    /// <summary><paramref name="name"/>, or <c>name2</c>, <c>name3</c>… if taken.</summary>
    public static string FreeName(SavedFlock flock, string name)
    {
        var taken = flock.InputList.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++) candidate = name + n;
        return candidate;
    }
}
