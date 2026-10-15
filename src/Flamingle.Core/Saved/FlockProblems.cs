namespace Flamingle.Core.Saved;

/// <summary>Something that will stop a saved flock loading the way it reads.</summary>
/// <param name="Window">The window's index in <see cref="SavedFlock.Windows"/>, when it's about one.</param>
/// <param name="Input">The input's name, when it's about one.</param>
public sealed record FlockProblem(string Message, int? Window = null, string? Input = null);

public static class FlockProblems
{
    /// <summary>Everything wrong with a saved flock's inputs and recipes — the designer shows these inline and
    /// won't load a flock that has any.</summary>
    public static IReadOnlyList<FlockProblem> Find(SavedFlock flock)
    {
        var problems = new List<FlockProblem>();
        var inputs = flock.InputList;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            if (!FlockInput.IsValidName(input.Name))
                problems.Add(new($"\"{input.Name}\" can't be an input name — use letters, digits and _, not starting with a digit.", Input: input.Name));
            else if (!names.Add(input.Name))
                problems.Add(new($"Two inputs are called \"{input.Name}\".", Input: input.Name));

            if (input.Kind == InputKind.Computed)
            {
                if (string.IsNullOrWhiteSpace(input.Default))
                    problems.Add(new($"\"{input.Label}\" has no command to work it out.", Input: input.Name));
                foreach (var unknown in Unknown(input.Default, inputs))
                    problems.Add(new($"\"{input.Label}\" uses ${{{unknown}}}, which isn't an input.", Input: input.Name));
            }
            else if (input.Kind == InputKind.Choice && input.Choices().Count == 0)
            {
                problems.Add(new($"\"{input.Label}\" has no choices.", Input: input.Name));
            }
        }

        var loop = InputValues.InLoops(inputs);
        if (loop.Count > 0)
            problems.Add(new($"{string.Join(" and ", loop)} each need the other to be worked out first.", Input: loop[0]));

        for (var i = 0; i < flock.Windows.Count; i++)
        {
            var w = flock.Windows[i];
            var recipe = w.EffectiveRecipe();
            if (recipe.Kind == LaunchKind.Skip) continue;

            if (recipe.Launches && string.IsNullOrWhiteSpace(recipe.Target))
                problems.Add(new(recipe.Kind == LaunchKind.StoreApp
                    ? $"{w.ProcessName} has no app id to start."
                    : $"{w.ProcessName} has no program to start.", Window: i));
            if (recipe.WaitSeconds is < 1 or > LaunchRecipe.MaxWaitSeconds)
                problems.Add(new($"{w.ProcessName}'s wait must be between 1 and {LaunchRecipe.MaxWaitSeconds} seconds.", Window: i));

            var unknown = recipe.Templates().SelectMany(t => Unknown(t, inputs))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var name in unknown)
                problems.Add(new($"{w.ProcessName} uses ${{{name}}}, which isn't an input.", Window: i));
        }
        return problems;
    }

    private static IEnumerable<string> Unknown(string template, IReadOnlyList<FlockInput> inputs)
        => Placeholders.NamesIn(template)
            .Where(n => !inputs.Any(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase)));
}
