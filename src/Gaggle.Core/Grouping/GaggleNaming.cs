namespace Gaggle.Core.Grouping;

public static class GaggleNaming
{
    /// <summary>
    /// A default name from the members' apps, e.g. <c>"Code + msedge + WindowsTerminal"</c> — up to three
    /// distinct apps, then <c>"+N"</c>. Falls back to <c>"Gaggle {n}"</c> when there's nothing to go on.
    /// </summary>
    public static string Suggest(IEnumerable<string> processNames, int fallbackNumber)
    {
        var apps = processNames
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (apps.Count == 0) return $"Gaggle {fallbackNumber}";

        var name = string.Join(" + ", apps.Take(3));
        return apps.Count > 3 ? $"{name} +{apps.Count - 3}" : name;
    }
}
