using Flamingle.Core.Changelog;

namespace Flamingle.Tests;

public class ChangelogParserTests
{
    private const string Sample = """
# Changelog

Some preamble.

---

## [Unreleased]

- work in progress

---

## [v0.1.2] - 2026-10-22

- Flocks stop wandering between desktops.

---

## [v0.1.1] - 2026-10-21

- Zones on every monitor.
- Gaps are a separate toggle.

---

## [v0.1.0] - 2026-10-20

- Flocks.

---
""";

    [Fact]
    public void Parse_returns_a_section_per_heading_skipping_the_title()
    {
        var sections = ChangelogParser.Parse(Sample);

        // Unreleased + three versions (the "# Changelog" title isn't a "## " section).
        Assert.Equal(4, sections.Count);
        Assert.Null(sections[0].Version);
        Assert.Equal("v0.1.2", sections[1].Display);
        Assert.Equal(new Version(0, 1, 2), sections[1].Version);
    }

    [Fact]
    public void Parse_trims_the_trailing_rule_and_blank_lines()
    {
        var section = ChangelogParser.Parse(Sample)[1];

        Assert.Equal("## [v0.1.2] - 2026-10-22", section.Block[0]);
        Assert.DoesNotContain("---", section.Block);
        Assert.NotEqual("", section.Block[^1].Trim());
    }

    [Fact]
    public void Parse_handles_crlf()
    {
        var section = ChangelogParser.Parse(Sample.ReplaceLineEndings("\r\n"))[2];

        Assert.Equal(new[] { "## [v0.1.1] - 2026-10-21", "", "- Zones on every monitor.", "- Gaps are a separate toggle." }, section.Block);
    }

    [Fact]
    public void One_step_shows_one_release()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "v0.1.1", "v0.1.2");

        Assert.Equal("v0.1.2", Assert.Single(unseen).Display);
    }

    [Fact]
    public void Several_steps_show_every_release_between_newest_first_without_unreleased()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "v0.0.9", "v0.1.2");

        Assert.Equal(new[] { "v0.1.2", "v0.1.1", "v0.1.0" }, unseen.Select(s => s.Display));
    }

    [Fact]
    public void Releases_newer_than_the_running_version_are_left_out()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "v0.0.9", "v0.1.1");

        Assert.Equal(new[] { "v0.1.1", "v0.1.0" }, unseen.Select(s => s.Display));
    }

    [Fact]
    public void Same_version_shows_nothing()
        => Assert.Empty(ChangelogParser.UnseenSince(Sample, "v0.1.2", "v0.1.2"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    public void No_usable_last_seen_version_shows_nothing(string? lastSeen)
        => Assert.Empty(ChangelogParser.UnseenSince(Sample, lastSeen, "v0.1.2"));

    [Fact]
    public void The_leading_v_is_optional()
    {
        var unseen = ChangelogParser.UnseenSince(Sample, "0.1.1", "0.1.2");

        Assert.Equal("v0.1.2", Assert.Single(unseen).Display);
    }
}
