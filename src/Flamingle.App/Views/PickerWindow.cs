using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.Core.Grouping;
using Flamingle.Core.Windows;

namespace Flamingle.App.Views;

/// <summary>
/// "New flock": tick the windows that belong together and name the group. Opened from the new-flock
/// hotkey with the snap-group guess pre-checked, or from settings' "New flock…". Built in code —
/// the layout is still in flux. Enter creates, Esc cancels.
/// </summary>
internal sealed class PickerWindow : Window
{
    public sealed record Row(WindowSnapshot Window, bool PreChecked, string? CurrentFlock);

    private readonly List<(CheckBox Box, WindowSnapshot Window)> _rows = [];
    private readonly TextBox _name;
    private readonly TextBlock _count;
    private readonly Button _create;
    private readonly int _fallbackNumber;
    private string? _lastSuggestion;

    /// <summary>Raised with the chosen name and windows when the user clicks Create.</summary>
    public event Action<string, IReadOnlyList<WindowSnapshot>>? Confirmed;

    public PickerWindow(IReadOnlyList<Row> rows, int fallbackNumber, bool fromSnapGuess)
    {
        _fallbackNumber = fallbackNumber;

        Title = "New flock";
        Icon = TrayIconArt.Product();
        Width = 600;
        Height = 560;
        MinWidth = 420;
        MinHeight = 320;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Panel;

        var subtitle = fromSnapGuess && rows.Count(r => r.PreChecked) > 1
            ? "Pre-checked: the windows that look snapped together with the one you were using. Adjust, then create."
            : "Tick the windows that should minimise and restore together.";

        _name = new TextBox { Margin = new Thickness(0, 4, 0, 0) };

        var list = new StackPanel { Spacing = 4 };
        // Pre-checked rows first, then the rest in Z order.
        foreach (var row in rows.OrderByDescending(r => r.PreChecked))
            list.Children.Add(BuildRow(row));

        _count = new TextBlock { Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };

        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0) };
        cancel.Click += (_, _) => Close();

        _create = new Button { Content = "Create flock", Margin = new Thickness(8, 0, 0, 0) };
        _create.Classes.Add("accent");
        _create.Click += (_, _) => Confirm();

        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = { cancel, _create } };
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(_count);

        var header = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 0, 12),
            Children =
            {
                new TextBlock { Text = "New flock", FontSize = 20, FontWeight = FontWeight.SemiBold, Foreground = Palette.Fg },
                new TextBlock { Text = subtitle, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Name", Foreground = Palette.Muted, Margin = new Thickness(0, 10, 0, 0) },
                _name,
            },
        };

        var root = new DockPanel { Margin = new Thickness(18) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = list });
        Content = root;

        Refresh();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Activate();
        _create.Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Confirm(); e.Handled = true; }
    }

    private Control BuildRow(Row row)
    {
        var w = row.Window;
        var details = new List<string>();
        if (!string.IsNullOrEmpty(w.ProcessName)) details.Add(w.ProcessName);
        details.Add(w.IsMinimised ? "minimised" : w.IsMaximised ? "maximised" : $"{w.Bounds.Width}×{w.Bounds.Height}");
        if (row.CurrentFlock is not null) details.Add($"in \"{row.CurrentFlock}\" — will move");

        var box = new CheckBox
        {
            IsChecked = row.PreChecked,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = w.Title, Foreground = Palette.Fg, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = string.Join("  ·  ", details), Foreground = Palette.Muted, FontSize = 12 },
                },
            },
        };
        box.IsCheckedChanged += (_, _) => Refresh();
        _rows.Add((box, w));

        return new Border
        {
            Background = Palette.Row,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 6),
            Child = box,
        };
    }

    private IReadOnlyList<WindowSnapshot> Checked()
        => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Window).ToList();

    private void Refresh()
    {
        var picked = Checked();
        _count.Text = picked.Count == 1 ? "1 window" : $"{picked.Count} windows";
        _create.IsEnabled = picked.Count > 0;

        // Keep suggesting a name from the picked apps until the user types their own.
        var userTyped = _name.Text is { Length: > 0 } text && text != _lastSuggestion;
        if (!userTyped)
        {
            _lastSuggestion = FlockNaming.Suggest(picked.Select(w => w.ProcessName), _fallbackNumber);
            _name.Text = _lastSuggestion;
        }
    }

    private void Confirm()
    {
        var picked = Checked();
        if (picked.Count == 0) return;

        var name = string.IsNullOrWhiteSpace(_name.Text)
            ? FlockNaming.Suggest(picked.Select(w => w.ProcessName), _fallbackNumber)
            : _name.Text.Trim();
        Confirmed?.Invoke(name, picked);
        Close();
    }
}
