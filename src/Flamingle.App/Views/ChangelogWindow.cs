using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Views.Settings;
using Flamingle.Core.Changelog;

namespace Flamingle.App.Views;

/// <summary>
/// The post-update "what's new" card, centred on screen: a headline, the changelog sections released since the
/// version that last ran here (scrolling), and two buttons — Close, and "Don't show changelogs again", which turns
/// the pop-up off. Shown once per update by the startup check; the sections are picked by
/// <see cref="ChangelogParser"/>. Ported from Perch.
/// </summary>
internal sealed class ChangelogWindow : Window
{
    public ChangelogWindow(string subhead, IReadOnlyList<ChangelogSection> sections, Action onSuppress)
    {
        Title = "What's new in Flamingle";
        Icon = TrayIconArt.Product();
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        CanResize = false;
        Width = 520;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var heading = new StackPanel
        {
            Children =
            {
                new TextBlock { Text = Title, FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title },
                new TextBlock
                {
                    Text = subhead, FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 0),
                },
            },
        };
        var closeGlyph = new Button
        {
            Content = "✕", Foreground = Palette.Muted, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(4, 0), FontSize = 14, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, Cursor = new Cursor(StandardCursorType.Hand),
        };
        closeGlyph.Click += (_, _) => Close();
        var header = new Grid { Children = { heading, closeGlyph } };

        var body = new StackPanel();
        if (sections.Count == 0) body.Children.Add(SettingsUi.BodyText("No changelog entries in that range."));
        for (var i = 0; i < sections.Count; i++)
        {
            if (i > 0) body.Children.Add(SettingsUi.Divider());
            ChangelogMarkdown.Render(body, sections[i].Block);
        }
        var scroller = new ScrollViewer
        {
            Content = body, Margin = new Thickness(0, 8, 0, 12),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var suppress = SettingsUi.FlatButton("Don't show changelogs again", () =>
        {
            onSuppress();
            Close();
        });
        var close = SettingsUi.FlatButton("Close", Close);
        close.Background = Palette.AccentBrush;
        close.Foreground = Palette.OnAccent;
        close.MinWidth = 84;
        var buttons = SettingsUi.ButtonRow(suppress, close);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;

        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(22) };
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        grid.Children.Add(header);
        grid.Children.Add(scroller);
        grid.Children.Add(buttons);

        var card = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Child = grid,
        };
        // No title bar, so the card itself drags the window.
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };
        Content = card;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }
}
