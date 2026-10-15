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
    OpenSettings,
}

/// <summary>What a panel's Flock section shows: the name of the layout's flock on the desktop in view (null if it
/// has none) and the saved flocks that can be loaded.</summary>
internal sealed record PanelFlocks(string? FlockName, IReadOnlyList<SavedFlock> Saved);

/// <summary>
/// A layout's panel, shown at the top of the layout while it's in edit mode: undo / redo and settings, the layout
/// presets, the layout's actions grouped by what they act on (splits, frame, windows), and the layout's flock (rename,
/// save, load). Never activated (so it doesn't pull focus from the layout's windows) — except while a name is being
/// typed; topmost while it's up.
/// </summary>
internal sealed class LayoutPanelWindow : Window
{
    private const double ContentWidth = 340;

    // Segoe Fluent Icons on Windows 11, MDL2 Assets on Windows 10 — same code points.
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private const string UndoGlyph = "";
    private const string RedoGlyph = "";
    private const string SettingsGlyph = "";
    private const string RenameGlyph = "";
    private const string SaveGlyph = "";
    private const string LoadGlyph = "";

    private enum Mode { Idle, Renaming, Saving, Loading }

    private readonly TextBlock _label = new()
    {
        Foreground = Palette.Fg, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly WrapPanel _presets = new() { MaxWidth = ContentWidth };
    private readonly Button _adjust;
    private readonly Button _undo;
    private readonly Button _redo;

    private readonly TextBlock _flockName = new()
    {
        Foreground = Palette.Fg, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly Grid _flockRow = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
    private readonly Button _rename;
    private readonly Button _save;
    private readonly Button _load;

    private readonly TextBox _nameBox = new() { FontSize = 12, Width = 200, Margin = new Thickness(0, 0, 4, 4) };
    private readonly Button _commitName = TextButton("Save", () => { });
    private readonly WrapPanel _nameEditor = new() { MaxWidth = ContentWidth, IsVisible = false };
    private readonly StackPanel _chooser = new() { Spacing = 4, MaxWidth = ContentWidth, IsVisible = false };

    private string? _currentLayout;
    private PanelFlocks _flocks = new(null, []);
    private Mode _mode = Mode.Idle;

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

        _undo = IconButton(UndoGlyph, "Undo (Ctrl+Z)", () => ActionChosen?.Invoke(PanelAction.Undo));
        _redo = IconButton(RedoGlyph, "Redo (Ctrl+Y)", () => ActionChosen?.Invoke(PanelAction.Redo));
        var settings = IconButton(SettingsGlyph, "Settings", () => ActionChosen?.Invoke(PanelAction.OpenSettings));
        _adjust = ActionButton("Adjust splits", PanelAction.AdjustSplits);

        _rename = IconButton(RenameGlyph, "Rename this flock", () => Begin(Mode.Renaming));
        _save = IconButton(SaveGlyph, "Save this flock", () => Begin(Mode.Saving));
        _load = IconButton(LoadGlyph, "Load a saved flock", () => Begin(Mode.Loading));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(_label);
        var headerIcons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(12, 0, 0, 0) };
        headerIcons.Children.Add(_undo);
        headerIcons.Children.Add(_redo);
        headerIcons.Children.Add(settings);
        Grid.SetColumn(headerIcons, 1);
        header.Children.Add(headerIcons);

        _flockRow.Children.Add(_flockName);
        var flockIcons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(8, 0, 0, 0) };
        flockIcons.Children.Add(_rename);
        flockIcons.Children.Add(_save);
        flockIcons.Children.Add(_load);
        Grid.SetColumn(flockIcons, 1);
        _flockRow.Children.Add(flockIcons);

        Content = new Border
        {
            Background = Palette.ToastBg,
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 12, 10),
            Child = new StackPanel
            {
                Spacing = 8,
                Width = ContentWidth,
                Children =
                {
                    header,
                    Caption("Layout"),
                    _presets,
                    Rule(),
                    ActionRow("Splits", _adjust, ActionButton("Even out", PanelAction.EvenOutSplits)),
                    ActionRow("Frame", ActionButton("Fill screen", PanelAction.FillScreen)),
                    ActionRow("Windows",
                        ActionButton("Fill with open windows", PanelAction.FillWithOpenWindows),
                        ActionButton("Minimise", PanelAction.MinimiseLayout),
                        ActionButton("Release", PanelAction.ReleaseWindows)),
                    Rule(),
                    Caption("Flock"),
                    _flockRow,
                    _nameEditor,
                    _chooser,
                },
            },
        };
        BuildPresets();

        _commitName.Click += (_, _) => End(commit: true);
        _nameEditor.Children.Add(_nameBox);
        _nameEditor.Children.Add(_commitName);
        _nameEditor.Children.Add(TextButton("Cancel", () => End(commit: false)));
        // Tunnel, so Enter / Esc are ours before the text box sees them.
        _nameBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            End(commit: e.Key == Key.Enter);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { if (_mode is Mode.Renaming or Mode.Saving) End(commit: false); };

        SetFlocks(_flocks);
    }

    public event Action<string>? LayoutChosen;

    public event Action<PanelAction>? ActionChosen;

    /// <summary>The layout's flock was renamed from the panel (new name).</summary>
    public event Action<string>? FlockRenamed;

    /// <summary>The layout's flock was saved from the panel, under this name.</summary>
    public event Action<string>? FlockSaved;

    /// <summary>A saved flock was picked to load onto this layout.</summary>
    public event Action<Guid>? FlockLoaded;

    /// <summary>The panel took (true) or gave back (false) the keyboard for typing a name — edit mode's own keys
    /// (Esc, Ctrl+Z / Ctrl+Y) must let it have them meanwhile.</summary>
    public event Action<bool>? TypingChanged;

    /// <summary>The layout's description — the panel's title.</summary>
    public void SetLabel(string text) => _label.Text = text;

    /// <summary>Whether the divider strips are showing (the Adjust splits button reads "Done adjusting").</summary>
    public void SetAdjusting(bool on) => _adjust.Content = on ? "Done adjusting" : "Adjust splits";

    /// <summary>Enable Undo / Redo when there's something to undo / redo.</summary>
    public void SetHistory(bool canUndo, bool canRedo)
    {
        _undo.IsEnabled = canUndo;
        _redo.IsEnabled = canRedo;
    }

    /// <summary>Highlight the current layout among the presets.</summary>
    public void SetCurrentLayout(string layoutId)
    {
        if (_currentLayout == layoutId) return;
        _currentLayout = layoutId;
        BuildPresets();
    }

    /// <summary>Show the layout's flock, and keep the saved flocks to offer for loading.</summary>
    public void SetFlocks(PanelFlocks flocks)
    {
        _flocks = flocks;
        var has = flocks.FlockName is not null;
        _flockName.Text = flocks.FlockName ?? "None yet — drop windows into the zones";
        _flockName.Foreground = has ? Palette.Fg : Palette.Muted;
        _rename.IsEnabled = has;
        _save.IsEnabled = has;
        _load.IsEnabled = flocks.Saved.Count > 0;
        ToolTip.SetTip(_load, flocks.Saved.Count > 0 ? "Load a saved flock" : "No saved flocks yet");

        if (!has && _mode is Mode.Renaming or Mode.Saving) End(commit: false);
        if (_mode == Mode.Loading)
        {
            if (flocks.Saved.Count == 0) End(commit: false);
            else BuildChooser();
        }
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

    // ---- The flock row: rename / save (a name field) and load (a list of saved flocks) ----

    private void Begin(Mode mode)
    {
        if (mode is Mode.Renaming or Mode.Saving && _flocks.FlockName is null) return;
        var wasTyping = _mode is Mode.Renaming or Mode.Saving;
        if (_mode == Mode.Loading) _chooser.IsVisible = false;
        _mode = mode;
        _flockRow.IsVisible = false;

        if (mode == Mode.Loading)
        {
            if (wasTyping) StopTyping();
            BuildChooser();
            _chooser.IsVisible = true;
            return;
        }

        _commitName.Content = mode == Mode.Renaming ? "Rename" : "Save";
        _nameBox.Text = _flocks.FlockName;
        _nameEditor.IsVisible = true;
        SetActivatable(true);
        Activate();
        _nameBox.Focus();
        _nameBox.SelectAll();
        if (!wasTyping) TypingChanged?.Invoke(true);
    }

    private void End(bool commit, Guid load = default)
    {
        var mode = _mode;
        if (mode == Mode.Idle) return;
        _mode = Mode.Idle;
        _flockRow.IsVisible = true;
        _chooser.IsVisible = false;

        if (mode == Mode.Loading)
        {
            if (commit) FlockLoaded?.Invoke(load);
            return;
        }

        var name = _nameBox.Text?.Trim();
        _nameEditor.IsVisible = false;
        StopTyping();
        if (!commit || string.IsNullOrEmpty(name)) return;
        if (mode == Mode.Renaming) FlockRenamed?.Invoke(name);
        else FlockSaved?.Invoke(name);
    }

    private void StopTyping()
    {
        _nameEditor.IsVisible = false;
        SetActivatable(false);
        TypingChanged?.Invoke(false);
    }

    private void BuildChooser()
    {
        _chooser.Children.Clear();
        _chooser.Children.Add(new TextBlock { Text = "Load which saved flock?", Foreground = Palette.Muted, FontSize = 11 });
        var list = new WrapPanel { MaxWidth = ContentWidth };
        foreach (var saved in _flocks.Saved)
        {
            var id = saved.Id;
            var button = TextButton(saved.Name, () => End(commit: true, load: id));
            ToolTip.SetTip(button, $"{saved.Windows.Count} window{(saved.Windows.Count == 1 ? "" : "s")} · saved {saved.SavedAt.LocalDateTime:d MMM HH:mm}");
            list.Children.Add(button);
        }
        list.Children.Add(TextButton("Cancel", () => End(commit: false)));
        _chooser.Children.Add(list);
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

    // ---- Building blocks ----

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

    /// <summary>A caption on the left, its buttons wrapping on the right.</summary>
    private static Grid ActionRow(string caption, params Button[] buttons)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("64,*") };
        var label = Caption(caption);
        label.Margin = new Thickness(0, 5, 0, 0);
        row.Children.Add(label);
        var wrap = new WrapPanel();
        foreach (var b in buttons) wrap.Children.Add(b);
        Grid.SetColumn(wrap, 1);
        row.Children.Add(wrap);
        return row;
    }

    private Button ActionButton(string text, PanelAction action) => TextButton(text, () => ActionChosen?.Invoke(action));

    private static Button TextButton(string text, Action onClick)
    {
        var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4) };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button IconButton(string glyph, string tip, Action onClick)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = glyph, FontFamily = IconFont, FontSize = 14 },
            Padding = new Thickness(7, 5),
            Background = Brushes.Transparent,
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => onClick();
        return button;
    }

    private static TextBlock Caption(string text)
        => new() { Text = text, Foreground = Palette.Muted, FontSize = 11 };

    private static Border Rule() => new() { Height = 1, Background = new SolidColorBrush(Colors.White, 0.08) };

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
