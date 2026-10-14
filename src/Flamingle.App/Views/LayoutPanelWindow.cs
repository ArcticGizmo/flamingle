using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Flamingle.App.Native;
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

/// <summary>
/// A layout's panel, shown at the top of the layout while it's in edit mode: the layout presets, the gaps toggle and
/// the window actions. Never activated (so it doesn't pull focus from the layout's windows); topmost while it's up.
/// </summary>
internal sealed class LayoutPanelWindow : Window
{
    private readonly TextBlock _label = new() { Foreground = Palette.Fg, FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly WrapPanel _presets = new() { MaxWidth = 340 };
    private readonly CheckBox _gaps = new() { Content = "Gaps between windows", FontSize = 12 };
    private Button? _adjust;
    private Button? _undo;
    private Button? _redo;
    private bool _settingGaps;
    private string? _currentLayout;

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
                },
            },
        };
        BuildPresets();
        _gaps.IsCheckedChanged += (_, _) =>
        {
            if (!_settingGaps) GapsToggled?.Invoke(_gaps.IsChecked == true);
        };
    }

    public event Action<string>? LayoutChosen;

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

    private Button ActionButton(string text, PanelAction action)
    {
        var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4) };
        button.Click += (_, _) => ActionChosen?.Invoke(action);
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
