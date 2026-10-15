using System.Text;
using System.Text.RegularExpressions;

namespace Flamingle.Core.Saved;

/// <summary>What a piece of an expanded template is, for showing it: as written, an input's value, or a name
/// nothing defines.</summary>
public enum SegmentKind
{
    Text,
    Value,
    Unknown,
}

public sealed record Segment(string Text, SegmentKind Kind);

/// <summary>A template filled in: the text, the same text in display segments, and any names nothing defines (those
/// stay as written).</summary>
public sealed record Expansion(string Text, IReadOnlyList<Segment> Segments, IReadOnlyList<string> Unknown)
{
    /// <summary>Did it use any input at all?</summary>
    public bool UsesValues => Segments.Any(s => s.Kind != SegmentKind.Text);
}

/// <summary>
/// <c>${name}</c> placeholders in saved-flock recipes — VS Code-style:
/// <list type="bullet">
/// <item><c>${name}</c> — an input's value (names are case-insensitive);</item>
/// <item><c>${name.name}</c> / <c>${name.parent}</c> — a path value's last part / the folder above it;</item>
/// <item><c>${env:VAR}</c> — an environment variable;</item>
/// <item><c>$${</c> — a literal <c>${</c>.</item>
/// </list>
/// </summary>
public static class Placeholders
{
    private static readonly Regex Token = new(
        @"\$\$\{|\$\{(?:env:(?<env>[A-Za-z_][A-Za-z0-9_]*)|(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\.(?<part>name|parent))?)\}",
        RegexOptions.CultureInvariant);

    /// <summary>Fill in <paramref name="template"/>. <paramref name="environment"/> reads environment variables
    /// (default: the process's).</summary>
    public static Expansion Expand(string template, IReadOnlyDictionary<string, string> values,
        Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) lookup.TryAdd(k, v);   // the first wins if names differ only in case

        var text = new StringBuilder();
        var segments = new List<Segment>();
        var unknown = new List<string>();
        var plain = new StringBuilder();

        void Flush()
        {
            if (plain.Length == 0) return;
            segments.Add(new Segment(plain.ToString(), SegmentKind.Text));
            plain.Clear();
        }

        void Add(string s, SegmentKind kind)
        {
            text.Append(s);
            if (kind == SegmentKind.Text)
            {
                plain.Append(s);
                return;
            }
            Flush();
            segments.Add(new Segment(s, kind));
        }

        var last = 0;
        foreach (Match m in Token.Matches(template))
        {
            if (m.Index > last) Add(template[last..m.Index], SegmentKind.Text);
            last = m.Index + m.Length;

            if (m.Value == "$${")
            {
                Add("${", SegmentKind.Text);
                continue;
            }

            string? value;
            string name;
            if (m.Groups["env"].Success)
            {
                name = "env:" + m.Groups["env"].Value;
                value = environment(m.Groups["env"].Value);
            }
            else
            {
                name = m.Groups["name"].Value;
                value = lookup.TryGetValue(name, out var v) ? Part(v, m.Groups["part"].Value) : null;
            }

            if (value is null)
            {
                Add(m.Value, SegmentKind.Unknown);
                if (!unknown.Contains(name, StringComparer.OrdinalIgnoreCase)) unknown.Add(name);
            }
            else
            {
                Add(value, SegmentKind.Value);
            }
        }
        if (last < template.Length) Add(template[last..], SegmentKind.Text);
        Flush();

        return new Expansion(text.ToString(), segments, unknown);
    }

    /// <summary>The input names a template uses (not environment variables), each once.</summary>
    public static IReadOnlyList<string> NamesIn(string template)
        => Token.Matches(template)
            .Where(m => m.Groups["name"].Success)
            .Select(m => m.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool Uses(string template, string name)
        => NamesIn(template).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>Point every use of <paramref name="from"/> at <paramref name="to"/>, keeping any <c>.name</c> /
    /// <c>.parent</c>.</summary>
    public static string Rename(string template, string from, string to)
        => Token.Replace(template, m =>
            m.Groups["name"].Success && string.Equals(m.Groups["name"].Value, from, StringComparison.OrdinalIgnoreCase)
                ? "${" + to + (m.Groups["part"].Success ? "." + m.Groups["part"].Value : "") + "}"
                : m.Value);

    /// <summary>A path's last part or parent folder; the value itself for no part.</summary>
    private static string Part(string value, string part)
    {
        if (part.Length == 0) return value;
        var trimmed = value.TrimEnd('\\', '/');
        return part == "name"
            ? Path.GetFileName(trimmed)
            : Path.GetDirectoryName(trimmed) ?? "";
    }
}
