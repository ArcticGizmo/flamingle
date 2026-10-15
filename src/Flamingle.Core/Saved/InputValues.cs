namespace Flamingle.Core.Saved;

/// <summary>Every input's value for one load, and the computed inputs that couldn't be worked out.</summary>
public sealed record ResolvedInputs(IReadOnlyDictionary<string, string> Values, IReadOnlyList<string> Failed);

public static class InputValues
{
    /// <summary>
    /// Work out every input's value for a load: what the user entered (else the default) for asked-for inputs, then
    /// each computed input's command — in dependency order, so one may use another — run through
    /// <paramref name="runCommand"/> (the expanded command → its output, or null if it failed). A computed input
    /// that fails, uses an unknown name or sits in a loop gets an empty value and is listed in
    /// <see cref="ResolvedInputs.Failed"/>.
    /// </summary>
    public static ResolvedInputs Resolve(IReadOnlyList<FlockInput> inputs, IReadOnlyDictionary<string, string> entered,
        Func<string, string?> runCommand)
    {
        var typed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in entered) typed.TryAdd(k, v);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs.Where(i => i.Kind != InputKind.Computed))
        {
            var value = typed.TryGetValue(input.Name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : input.DefaultValue();
            values.TryAdd(input.Name, value);
        }

        var failed = new List<string>();
        var order = ComputedOrder(inputs, out var stuck);
        foreach (var input in order)
        {
            var command = Placeholders.Expand(input.Default, values);
            var output = command.Unknown.Count == 0 && command.Text.Trim().Length > 0 ? runCommand(command.Text) : null;
            if (output is null) failed.Add(input.Name);
            values.TryAdd(input.Name, FirstLine(output));
        }
        foreach (var input in stuck)
        {
            failed.Add(input.Name);
            values.TryAdd(input.Name, "");
        }
        return new ResolvedInputs(values, failed);
    }

    /// <summary>The computed inputs that use each other in a loop (so can never be worked out).</summary>
    public static IReadOnlyList<string> InLoops(IReadOnlyList<FlockInput> inputs)
    {
        ComputedOrder(inputs, out var stuck);
        return stuck.Select(i => i.Name).ToList();
    }

    /// <summary>Computed inputs ordered so each comes after the computed inputs its command uses; the ones caught in
    /// a loop (or depending on one) are left in <paramref name="stuck"/>.</summary>
    private static List<FlockInput> ComputedOrder(IReadOnlyList<FlockInput> inputs, out List<FlockInput> stuck)
    {
        var computed = inputs.Where(i => i.Kind == InputKind.Computed).ToList();
        var computedNames = computed.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var order = new List<FlockInput>();

        var pending = computed.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.FirstOrDefault(i => Placeholders.NamesIn(i.Default)
                .All(n => !computedNames.Contains(n) || done.Contains(n)));
            if (ready is null) break;
            pending.Remove(ready);
            done.Add(ready.Name);
            order.Add(ready);
        }
        stuck = pending;
        return order;
    }

    private static string FirstLine(string? output)
        => output?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
}
