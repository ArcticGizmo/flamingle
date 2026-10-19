using System.Text.RegularExpressions;

namespace Flamingle.Core.Changelog;

/// <summary>
/// One version block from <c>CHANGELOG.md</c>: the raw markdown lines under a <c>## </c> heading (heading included,
/// trailing blank/rule lines trimmed) plus the version parsed out of that heading. Unversioned sections (e.g.
/// <c>## [Unreleased]</c>) keep a null <see cref="Version"/> so they can be parsed but never count as "what's new".
/// </summary>
public sealed record ChangelogSection(string Heading, string Display, Version? Version, IReadOnlyList<string> Block);

/// <summary>
/// Splits the (embedded) <c>CHANGELOG.md</c> into per-version sections and picks out the ones newer than a given
/// "last seen" version. Headings look like <c>## [v0.1.1] - 2026-10-20</c>; the <c>v0.1.1</c> is the version.
/// Ported from Perch.
/// </summary>
public static class ChangelogParser
{
    private static readonly Regex VersionRx = new(@"v?(\d+(?:\.\d+){1,3})", RegexOptions.Compiled);

    /// <summary>Every <c>## </c> section, in document order (newest first, as the file is written).</summary>
    public static IReadOnlyList<ChangelogSection> Parse(string markdown)
    {
        var sections = new List<ChangelogSection>();
        if (string.IsNullOrEmpty(markdown)) return sections;

        List<string>? block = null;
        var heading = "";

        void Flush()
        {
            if (block is null) return;
            // Drop trailing blank lines and the "---" rule that separates this section from the next.
            var end = block.Count;
            while (end > 0 && (block[end - 1].Trim().Length == 0 || block[end - 1].Trim() == "---")) end--;
            TryParseVersion(heading, out var version, out var display);
            sections.Add(new ChangelogSection(heading, display, version, block.GetRange(0, end)));
        }

        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## "))
            {
                Flush();
                heading = line[3..].Trim();
                block = [line];
            }
            else
            {
                block?.Add(line);
            }
        }
        Flush();
        return sections;
    }

    /// <summary>
    /// The sections strictly newer than <paramref name="lastSeen"/> and no newer than <paramref name="current"/>,
    /// newest first. Nothing when <paramref name="lastSeen"/> is blank or unparseable (a fresh install has no history
    /// to diff against) or equals <paramref name="current"/>. Unversioned sections are never included.
    /// </summary>
    public static IReadOnlyList<ChangelogSection> UnseenSince(string markdown, string? lastSeen, string current)
    {
        if (TryParse(lastSeen) is not { } low) return [];
        var high = TryParse(current);   // null → no upper bound (defensive)

        return Parse(markdown)
            .Where(s => s.Version is { } v && v > low && (high is null || v <= high))
            .ToList();
    }

    /// <summary>Pulls a <see cref="System.Version"/> out of a heading like <c>[v0.1.1] - 2026-10-20</c>.</summary>
    public static bool TryParseVersion(string heading, out Version? version, out string display)
    {
        version = null;
        display = heading;
        var m = VersionRx.Match(heading ?? "");
        if (!m.Success || !System.Version.TryParse(m.Groups[1].Value, out var v)) return false;
        version = v;
        display = "v" + m.Groups[1].Value;
        return true;
    }

    private static Version? TryParse(string? version)
        => !string.IsNullOrWhiteSpace(version) && TryParseVersion(version, out var v, out _) ? v : null;
}
