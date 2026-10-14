using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Flamingle.Core.Hotkeys;

namespace Flamingle.App.Views.Settings;

/// <summary>
/// The settings window's building blocks â€” text, rows, buttons and the few custom controls â€” modelled on
/// Perch's settings surface (a name + muted description on the left, the control on the right, a divider under
/// each row), in Flamingle's palette.
/// </summary>
internal static class SettingsUi
{
    public static TextBlock PageTitle(string text) => new()
    {
        Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold,
        Foreground = Palette.Title, Margin = new Thickness(0, 0, 0, 4),
    };

    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold,
        Foreground = Palette.Title, Margin = new Thickness(0, 18, 0, 4),
    };

    public static TextBlock BodyText(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13,
        Foreground = Palette.Muted, Margin = new Thickness(0, 0, 0, 6),
    };

    public static Button FlatButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text, Background = Palette.ButtonBg, Foreground = Palette.Fg,
            BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 6), FontSize = 13,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    public static StackPanel ButtonRow(params Control[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 4) };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    /// <summary>A setting: name and description on the left, its control on the right, a divider beneath.
    /// A disabled row is dimmed (its control too).</summary>
    public static Border Row(string name, string description, Control right, bool enabled = true)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var left = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 16, 0) };
        left.Children.Add(new TextBlock
        {
            Text = name, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrEmpty(description))
        {
            left.Children.Add(new TextBlock
            {
                Text = description, FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap,
            });
        }
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);
        right.VerticalAlignment = VerticalAlignment.Center;
        right.IsEnabled = enabled;
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(2, 11),
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Opacity = enabled ? 1.0 : 0.5,
        };
    }

    /// <summary>A pill switch that calls <paramref name="onChanged"/> with the new value when clicked.</summary>
    public static PillToggle Toggle(bool on, Action<bool> onChanged)
    {
        var toggle = new PillToggle(on);
        toggle.Changed += onChanged;
        return toggle;
    }

    /// <summary>A joined one-of-N picker; the selected segment fills with the accent.</summary>
    public static Control Segmented(IReadOnlyList<string> labels, int selected, Action<int> onSelected)
    {
        var buttons = new List<Button>();
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        void Restyle()
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var on = i == selected;
                buttons[i].Background = on ? Palette.AccentBrush : Brushes.Transparent;
                buttons[i].Foreground = on ? Palette.OnAccent : Palette.Fg;
                buttons[i].FontWeight = on ? FontWeight.SemiBold : FontWeight.Normal;
            }
        }

        for (var i = 0; i < labels.Count; i++)
        {
            if (i > 0) panel.Children.Add(new Border { Width = 1, Background = Palette.Line });
            var index = i;
            var button = new Button
            {
                Content = labels[i], Height = 30, MinWidth = 72, Padding = new Thickness(12, 0),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0), FontSize = 12,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            button.Click += (_, _) =>
            {
                if (selected == index) return;
                selected = index;
                Restyle();
                onSelected(index);
            };
            buttons.Add(button);
            panel.Children.Add(button);
        }

        Restyle();
        return new Border
        {
            BorderBrush = Palette.Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            ClipToBounds = true, Background = Palette.ButtonBg, HorizontalAlignment = HorizontalAlignment.Left,
            Child = panel,
        };
    }
}

/// <summary>An on/off switch: a rounded track with a sliding knob, accent-filled when on. Space / Enter toggle it.</summary>
internal sealed class PillToggle : Control
{
    private bool _on;

    public PillToggle(bool on)
    {
        _on = on;
        Width = 44;
        Height = 24;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        GotFocus += (_, _) => InvalidateVisual();
        LostFocus += (_, _) => InvalidateVisual();
    }

    /// <summary>Raised with the new value when the user flips it.</summary>
    public event Action<bool>? Changed;

    private void Flip()
    {
        _on = !_on;
        InvalidateVisual();
        Changed?.Invoke(_on);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Flip();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsEnabled && e.Key is Key.Space or Key.Enter)
        {
            Flip();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(1, 1, Bounds.Width - 2, Bounds.Height - 2);
        var track = _on ? Palette.Accent : Palette.ToggleOff;
        ctx.DrawRectangle(new SolidColorBrush(track), null, new RoundedRect(rect, rect.Height / 2));

        var d = rect.Height - 6;
        var x = _on ? rect.Right - d - 3 : rect.Left + 3;
        var r = d / 2;
        ctx.DrawEllipse(new SolidColorBrush(_on ? Color.Parse("#0B1220") : Color.Parse("#E6EDF3")), null,
            new Point(x + r, rect.Top + 3 + r), r, r);

        if (IsFocused)
        {
            var ring = new Rect(0, 0, Bounds.Width, Bounds.Height).Deflate(0.5);
            ctx.DrawRectangle(null, new Pen(Palette.AccentBrush, 1), new RoundedRect(ring, ring.Height / 2));
        }
    }
}

/// <summary>
/// Records a global shortcut: click, then press modifiers + a key (letter, digit or F-key). Shows the combo at
/// rest and "Press keysâ€¦" while listening. A combo without a modifier is ignored; Esc or clicking away cancels.
/// Flamingle's own hotkeys are suspended while it listens (they'd swallow the keys otherwise).
/// </summary>
internal sealed class HotkeyCaptureButton : Button
{
    private readonly Action<bool> _suspendHotkeys;
    private string _current;
    private bool _listening;

    public HotkeyCaptureButton(string current, Action<bool> suspendHotkeys)
    {
        _current = current;
        _suspendHotkeys = suspendHotkeys;
        Width = 180;
        Background = Palette.ButtonBg;
        Foreground = Palette.Fg;
        BorderThickness = new Thickness(1);
        BorderBrush = Palette.Line;
        CornerRadius = new CornerRadius(4);
        Padding = new Thickness(12, 6);
        FontSize = 13;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Cursor = new Cursor(StandardCursorType.Hand);
        UpdateLabel();
        LostFocus += (_, _) => StopListening();
        DetachedFromVisualTree += (_, _) => StopListening();
    }

    /// <summary>Raised with the new combo (e.g. <c>"Alt+Shift+E"</c>).</summary>
    public event Action<string>? Captured;

    protected override void OnClick()
    {
        if (_listening) return;
        _listening = true;
        _suspendHotkeys(true);
        Content = "Press keysâ€¦";
        BorderBrush = Palette.AccentBrush;
        Focus();
    }

    private void StopListening()
    {
        if (!_listening) return;
        _listening = false;
        _suspendHotkeys(false);
        BorderBrush = Palette.Line;
        UpdateLabel();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!_listening) { base.OnKeyDown(e); return; }
        e.Handled = true;   // swallow everything (Esc mustn't close the window, Space mustn't re-click)

        if (e.Key == Key.Escape) { StopListening(); return; }
        if (KeyName(e.Key) is not { } key) return;   // a modifier on its own, or an unsupported key

        var mods = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) mods |= HotkeyModifiers.Control;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt)) mods |= HotkeyModifiers.Alt;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) mods |= HotkeyModifiers.Shift;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta)) mods |= HotkeyModifiers.Win;
        if (mods == HotkeyModifiers.None) return;   // a bare key can't be claimed system-wide

        _current = new Hotkey(mods, key).ToString();
        StopListening();
        Captured?.Invoke(_current);
    }

    private void UpdateLabel() => Content = Hotkey.TryParse(_current)?.ToString() ?? "Not set";

    private static string? KeyName(Key k) => k switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (k - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (k - Key.NumPad0))).ToString(),
        >= Key.F1 and <= Key.F24 => $"F{k - Key.F1 + 1}",
        _ => null,
    };
}
