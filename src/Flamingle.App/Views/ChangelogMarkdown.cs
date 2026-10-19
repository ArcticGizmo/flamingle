using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Flamingle.App.Views.Settings;

namespace Flamingle.App.Views;

/// <summary>
/// Reads the embedded <c>CHANGELOG.md</c> and renders its (light) markdown as a column of text — shared by Settings →
/// Changelog and the post-update <see cref="ChangelogWindow"/> so the two read the same. Handles only what the
/// changelog uses: <c>## </c>/<c>### </c> headings, <c>-</c>/<c>*</c> bullets, <c>&gt; </c> quotes, <c>---</c> rules,
/// and inline emphasis, code and links (shown as plain text).
/// </summary>
internal static class ChangelogMarkdown
{
    /// <summary>The changelog embedded at build time (csproj: <c>Flamingle.CHANGELOG.md</c>), or null.</summary>
    public static string? LoadEmbedded()
    {
        try
        {
            using var stream = typeof(ChangelogMarkdown).Assembly.GetManifestResourceStream("Flamingle.CHANGELOG.md");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One control per markdown line, appended to <paramref name="page"/>.</summary>
    public static void Render(StackPanel page, IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("## "))
                page.Children.Add(SettingsUi.SectionTitle(StripInline(line[3..])));
            else if (line.StartsWith("### "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[4..]), FontSize = 13, FontWeight = FontWeight.SemiBold,
                    Foreground = Palette.Title, Margin = new Thickness(0, 6, 0, 4),
                });
            else if (line.StartsWith("# ")) { /* the title is redundant here */ }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
                page.Children.Add(SettingsUi.BodyText("•  " + StripInline(line[2..])));
            else if (line.Trim() == "---")
                page.Children.Add(SettingsUi.Divider());
            else if (line.StartsWith("> "))
                page.Children.Add(new TextBlock
                {
                    Text = StripInline(line[2..]), TextWrapping = TextWrapping.Wrap, FontSize = 13,
                    FontStyle = FontStyle.Italic, Foreground = Palette.Muted, Margin = new Thickness(12, 0, 0, 6),
                });
            else if (line.Trim().Length > 0)
                page.Children.Add(SettingsUi.BodyText(StripInline(line)));
        }
    }

    /// <summary>Inline markdown (bold, italic, code, links) down to its display text.</summary>
    public static string StripInline(string text)
    {
        text = Regex.Replace(text, @"\*\*(.*?)\*\*", "$1");
        text = Regex.Replace(text, @"__(.*?)__", "$1");
        text = Regex.Replace(text, @"\*(.*?)\*", "$1");
        text = Regex.Replace(text, @"(?<!\w)_(.*?)_(?!\w)", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text;
    }
}
