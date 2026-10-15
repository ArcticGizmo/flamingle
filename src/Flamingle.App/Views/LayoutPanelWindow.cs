using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;
using Flamingle.Core.Saved;
using Flamingle.Core.Zones;

namespace Flamingle.App.Views;

/// <summary>The actions offered by a layout's panel.</summary>
internal enum PanelAction
{
    FillWithOpenWindows,
    FillScreen,
    MinimiseLayout,
    ReleaseWindows,
    EvenOutSplits,
    AdjustSplits,
    Undo,
    Redo,
}

/// <summary>What a panel's Flock section shows: the name of the layout's flock on the desktop in view (null if it
/// has none) and every saved flock.</summary>
internal sealed record PanelFlocks(string? FlockName, IReadOnlyList<SavedFlock> Saved);

/// <summary>
/// A layout's panel, shown at the top of the layout while it's in edit mode: the layout presets, the gaps toggle, the
/// window actions, and the layout's flock (rename it, save it) with the saved flocks. Never activated (so it doesn't
/// pull focus from the layout's windows) — except while a name is being typed; topmost while it's up.
/// </summary>
internal sealed class LayoutPanelWindow : Window
{
    private enum Naming { Rename, Save }

    private readonly TextBlock _label = new() { Foreground = Palette.Fg, FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly WrapPanel _presets = new() { MaxWidth = 340 };
    private readonly CheckBox _gaps = new() { Content = "Gaps between windows", FontSize = 12 };
    private readonly TextBlock _flockName = new() { Foreground = Palette.Fg, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WrapPanel _flockActions = new() { MaxWidth = 340 };
    private readonly TextBox _nameBox = new() { FontSize = 12, Width = 200, Margin = new Thickness(0, 0, 4, 4) };
    private readonly Button _commitName = new() { FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4) };
    private readonly WrapPanel _nameEditor = new() { MaxWidth = 340, IsVisible = false };
    private readonly StackPanel _savedList = new() { Spacing = 2, MaxWidth = 340 };
    private Button? _adjust;
    private Button? _undo;
    private Button? _redo;
    private Button? _rename;
    private Button? _save;
    private bool _settingGaps;
    private string? _currentLayout;
    private PanelFlocks _flocks = new(null, []);
    private Naming? _naming;

    public LayoutPanelWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            Background = Palette.ToastBg,
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 8),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    _label,
                    Caption("Layout"),
                    _presets,
                    _gaps,
                    Caption("Windows"),
                    new WrapPanel
                    {
                        MaxWidth = 340,
                        Children =
                        {
                            ActionButton("Fill with open windows", PanelAction.FillWithOpenWindows),
                            ActionButton("Fill screen", PanelAction.FillScreen),
                            ActionButton("Minimise layout", PanelAction.MinimiseLayout),
                            (_adjust = ActionButton("Adjust splits", PanelAction.AdjustSplits)),
                            ActionButton("Even out splits", PanelAction.EvenOutSplits),
                            ActionButton("Release windows", PanelAction.ReleaseWindows),
                        },
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Children =
                        {
                            (_undo = ActionButton("Undo  (Ctrl+Z)", PanelAction.Undo)),
                            (_redo = ActionButton("Redo  (Ctrl+Y)", PanelAction.Redo)),
                        },
                    },
                    Caption("Flock"),
                    _flockName,
                    _flockActions,
                    _nameEditor,
                    _savedList,
                },
            },
        };
        BuildPresets();
        _gaps.IsCheckedChanged += (_, _) =>
        {
            if (!_settingGaps) GapsToggled?.Invoke(_gaps.IsChecked == true);
        };

        _rename = SmallButton("Rename…", () => BeginNaming(Naming.Rename));
        _save = SmallButton("Save…", () => BeginNaming(Naming.Save));
        _flockActions.Children.Add(_rename);
        _flockActions.Children.Add(_save);

        _commitName.Click += (_, _) => EndNaming(commit: true);
        _nameEditor.Children.Add(_nameBox);
        _nameEditor.Children.Add(_commitName);
        _nameEditor.Children.Add(SmallButton("Cancel", () => EndNaming(commit: false)));
        // Tunnel, so Enter / Esc are ours before the text box sees them.
        _nameBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            EndNaming(commit: e.Key == Key.Enter);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => EndNaming(commit: false);

        SetFlocks(_flocks);
    }

    public event Action<string>? LayoutChosen;

    /// <summary>The layout's flock was renamed from the panel (new name).</summary>
    public event Action<string>? FlockRenamed;

    /// <summary>The layout's flock was saved from the panel, under this name.</summary>
    public event Action<string>? FlockSaved;

    /// <summary>A saved flock's ✕ was clicked.</summary>
    public event Action<Guid>? SavedFlockDeleted;

    /// <summary>The panel took (true) or gave back (false) the keyboard for typing a name — edit mode's own keys
    /// (Esc, Ctrl+Z / Ctrl+Y) must let it have them meanwhile.</summary>
    public event Action<bool>? TypingChanged;

    /// <summary>Show the layout's flock and the saved flocks.</summary>
    public void SetFlocks(PanelFlocks flocks)
    {
        _flocks = flocks;
        var has = flocks.FlockName is not null;
        _flockName.Text = flocks.FlockName ?? "No flock here yet — windows dropped into the zones form one.";
        _flockName.Foreground = has ? Palette.Fg : Palette.Muted;
        if (_rename is not null) _rename.IsEnabled = has;
        if (_save is not null) _save.IsEnabled = has;
        if (!has) EndNaming(commit: false);

        _savedList.Children.Clear();
        if (flocks.Saved.Count == 0) return;
        _savedList.Children.Add(Caption("Saved flocks"));
        foreach (var saved in flocks.Saved)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = $"{saved.Name} · {saved.Windows.Count} window{(saved.Windows.Count == 1 ? "" : "s")}",
                Foreground = Palette.Fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var id = saved.Id;
            var delete = SmallButton("✕", () => SavedFlockDeleted?.Invoke(id));
            delete.Margin = new Thickness(6, 0, 0, 0);
            delete.Padding = new Thickness(6, 1);
            ToolTip.SetTip(delete, $"Delete the saved flock \"{saved.Name}\"");
            Grid.SetColumn(delete, 1);
            row.Children.Add(delete);
            _savedList.Children.Add(row);
        }
    }

    /// <summary>Swap the flock buttons for a name field, and take the keyboard so it can be typed in.</summary>
    private void BeginNaming(Naming mode)
    {
        if (_flocks.FlockName is not { } name) return;
        var wasNaming = _naming is not null;
        _naming = mode;
        _commitName.Content = mode == Naming.Rename ? "Rename" : "Save";
        _nameBox.Text = name;
        _flockActions.IsVisible = false;
        _nameEditor.IsVisible = true;

        SetActivatable(true);
        Activate();
        _nameBox.Focus();
        _nameBox.SelectAll();
        if (!wasNaming) TypingChanged?.Invoke(true);
    }

    private void EndNaming(bool commit)
    {
        if (_naming is not { } mode) return;
        _naming = null;
        var name = _nameBox.Text?.Trim();
        _nameEditor.IsVisible = false;
        _flockActions.IsVisible = true;
        SetActivatable(false);
        TypingChanged?.Invoke(false);

        if (!commit || string.IsNullOrEmpty(name)) return;
        if (mode == Naming.Rename) FlockRenamed?.Invoke(name);
        else FlockSaved?.Invoke(name);
    }

    /// <summary>Normally the panel can't be activated (clicks never take focus from the layout's windows); while a
    /// name is typed it has to be.</summary>
    private void SetActivatable(bool on)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        ex = on ? ex & ~Win32.WS_EX_NOACTIVATE : ex | Win32.WS_EX_NOACTIVATE;
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    public event Action<PanelAction>? ActionChosen;

    /// <summary>The gaps toggle was flipped (new value).</summary>
    public event Action<bool>? GapsToggled;

    /// <summary>The layout's description — the panel's title.</summary>
    public void SetLabel(string text) => _label.Text = text;

    /// <summary>Whether the divider strips are showing (the Adjust splits button reads "Done adjusting").</summary>
    public void SetAdjusting(bool on)
    {
        if (_adjust is not null) _adjust.Content = on ? "Done adjusting" : "Adjust splits";
    }

    /// <summary>Enable Undo / Redo when there's something to undo / redo.</summary>
    public void SetHistory(bool canUndo, bool canRedo)
    {
        if (_undo is not null) _undo.IsEnabled = canUndo;
        if (_redo is not null) _redo.IsEnabled = canRedo;
    }

    /// <summary>Reflect the current gaps setting without raising <see cref="GapsToggled"/>.</summary>
    public void SetGaps(bool on)
    {
        if (_gaps.IsChecked == on) return;
        _settingGaps = true;
        _gaps.IsChecked = on;
        _settingGaps = false;
    }

    /// <summary>Highlight the current layout among the presets.</summary>
    public void SetCurrentLayout(string layoutId)
    {
        if (_currentLayout == layoutId) return;
        _currentLayout = layoutId;
        BuildPresets();
    }

    /// <summary>The panel's size in physical pixels at the given DPI scale.</summary>
    public PixelSize PixelSizeAt(double scale)
    {
        if (Content is not Control c) return default;
        c.Measure(Size.Infinity);
        return new PixelSize((int)Math.Ceiling(c.DesiredSize.Width * scale), (int)Math.Ceiling(c.DesiredSize.Height * scale));
    }

    /// <summary>Put the panel back on top of everything — straight to Win32, since Avalonia only re-applies
    /// <c>Topmost</c> when the value changes.</summary>
    public void KeepOnTop()
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
            Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    private void BuildPresets()
    {
        _presets.Children.Clear();
        foreach (var layout in ZoneLayout.Templates)
        {
            var button = new Button
            {
                Padding = new Thickness(4),
                Margin = new Thickness(0, 0, 4, 4),
                Background = Brushes.Transparent,
                Content = new LayoutThumbnail(layout, 48, 30, layout.Id == _currentLayout),
            };
            ToolTip.SetTip(button, layout.Name);
            var id = layout.Id;
            button.Click += (_, _) => LayoutChosen?.Invoke(id);
            _presets.Children.Add(button);
        }
    }

    private Button ActionButton(string text, PanelAction action) => SmallButton(text, () => ActionChosen?.Invoke(action));

    private static Button SmallButton(string text, Action onClick)
    {
        var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock Caption(string text)
        => new() { Text = text, Foreground = Palette.Muted, FontSize = 11 };

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            // Takes the mouse, never activation; and stays out of Alt+Tab.
            var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
        }
    }
}
