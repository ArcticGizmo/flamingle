using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Gaggle.App.Native;
using Gaggle.Core.Zones;

namespace Gaggle.App.Views;

/// <summary>The actions offered by an expanded layout chip.</summary>
internal enum ChipAction
{
    FillWithOpenWindows,
    FillScreen,
    MinimiseLayout,
    ReleaseWindows,
}

/// <summary>
/// A floating handle for a zone container.
/// <list type="bullet">
/// <item><see cref="Kind.Chip"/> — a small tab on the layout's top edge. Drag it to move the whole layout;
/// click it to expand a panel of layout presets and actions; double-click to fill the monitor.</item>
/// <item><see cref="Kind.Resize"/> — a grip in the layout's bottom-right corner. Drag to resize.</item>
/// </list>
/// Never activated (grabbing it doesn't pull focus from the layout's windows) and not topmost — the
/// service lifts it just above the layout when one of the layout's windows is in front, so other apps can
/// still cover it. Drag offsets are physical screen pixels, read straight from the cursor.
/// </summary>
internal sealed class ContainerHandleWindow : Window
{
    public enum Kind { Chip, Resize }

    // Movement (physical px) before a press on the chip counts as a drag rather than a click.
    private const int DragThreshold = 4;

    // The pull-tab: a trapezoid hanging from the layout's top edge, with a grip mark.
    private const double TabWidth = 64;
    private const double TabHeight = 20;

    private static readonly IBrush TabFill = new SolidColorBrush(Color.Parse("#E60D1218"));
    private static readonly IBrush TabFillHot = new SolidColorBrush(Color.Parse("#F21E2A36"));

    private readonly TextBlock _label = new() { Foreground = Palette.Fg, FontSize = 13, FontWeight = FontWeight.SemiBold };
    private readonly Border _panel = new() { IsVisible = false };
    private readonly WrapPanel _presets = new() { MaxWidth = 340 };
    private Path? _tab;

    private Win32.POINT _pressAt;
    private bool _pressed;
    private bool _dragging;
    private string? _currentLayout;

    public ContainerHandleWindow(Kind kind)
    {
        HandleKind = kind;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = kind == Kind.Chip ? BuildChip() : BuildGrip();
    }

    public Kind HandleKind { get; }

    public bool IsDragging => _dragging;

    public bool IsExpanded => _panel.IsVisible;

    public event Action? DragStarted;

    /// <summary>Total cursor offset since the drag started, in physical pixels.</summary>
    public event Action<int, int>? Dragged;

    public event Action? DragEnded;

    public event Action? DoubleClicked;

    public event Action<string>? LayoutChosen;

    public event Action<ChipAction>? ActionChosen;

    /// <summary>Raised when the chip expands or collapses (its size changes, so it needs re-positioning).</summary>
    public event Action? ExpandedChanged;

    /// <summary>The layout's description — the expanded panel's title and the tab's tooltip.</summary>
    public void SetLabel(string text)
    {
        if (_label.Text == text) return;
        _label.Text = text;
        if (_tab?.Parent is Control tab)
            ToolTip.SetTip(tab, $"{text}\nDrag to move the layout · click for layouts and actions · double-click to fill the screen");
    }

    /// <summary>Highlight the current layout among the presets.</summary>
    public void SetCurrentLayout(string layoutId)
    {
        if (_currentLayout == layoutId) return;
        _currentLayout = layoutId;
        BuildPresets();
    }

    public void Collapse() => SetExpanded(false);

    /// <summary>The handle's size in physical pixels at the given DPI scale.</summary>
    public PixelSize PixelSizeAt(double scale)
    {
        if (Content is not Control c) return default;
        c.Measure(Size.Infinity);
        return new PixelSize((int)Math.Ceiling(c.DesiredSize.Width * scale), (int)Math.Ceiling(c.DesiredSize.Height * scale));
    }

    private Control BuildChip()
    {
        // Trapezoid: full width along the layout's edge, tapering as it hangs down.
        _tab = new Path
        {
            Data = Geometry.Parse($"M 0.75,0 L {TabWidth - 0.75},0 L {TabWidth - 10},{TabHeight - 0.75} L 10,{TabHeight - 0.75} Z"),
            Fill = TabFill,
            Stroke = Palette.AccentBrush,
            StrokeThickness = 1.5,
            StrokeJoin = PenLineJoin.Round,
        };
        var tab = new Grid
        {
            Width = TabWidth,
            Height = TabHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.SizeAll),
            Background = Brushes.Transparent,   // hit-testable across the whole tab
            Children =
            {
                _tab,
                // The grip: two short bars, like a drawer pull.
                new StackPanel
                {
                    Spacing = 3,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, -2, 0, 0),
                    Children = { GripBar(), GripBar() },
                },
            },
        };
        tab.PointerEntered += (_, _) => _tab.Fill = TabFillHot;
        tab.PointerExited += (_, _) => _tab.Fill = TabFill;

        _panel.Margin = new Thickness(0, 4, 0, 0);
        _panel.Background = Palette.ToastBg;
        _panel.BorderBrush = Palette.AccentBrush;
        _panel.BorderThickness = new Thickness(1);
        _panel.CornerRadius = new CornerRadius(10);
        _panel.Padding = new Thickness(12, 10, 12, 8);
        _panel.Child = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _label,
                Caption("Layout"),
                _presets,
                Caption("Windows"),
                new WrapPanel
                {
                    MaxWidth = 340,
                    Children =
                    {
                        ActionButton("Fill with open windows", ChipAction.FillWithOpenWindows),
                        ActionButton("Fill screen", ChipAction.FillScreen),
                        ActionButton("Minimise layout", ChipAction.MinimiseLayout),
                        ActionButton("Release windows", ChipAction.ReleaseWindows),
                    },
                },
            },
        };
        BuildPresets();

        return new StackPanel { Children = { tab, _panel } };
    }

    private static Rectangle GripBar() => new()
    {
        Width = 16,
        Height = 2,
        RadiusX = 1,
        RadiusY = 1,
        Fill = Palette.AccentBrush,
    };

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

    private Button ActionButton(string text, ChipAction action)
    {
        var button = new Button { Content = text, FontSize = 12, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 4, 4) };
        button.Click += (_, _) =>
        {
            SetExpanded(false);
            ActionChosen?.Invoke(action);
        };
        return button;
    }

    private static TextBlock Caption(string text)
        => new() { Text = text, Foreground = Palette.Muted, FontSize = 11 };

    private static Control BuildGrip()
    {
        var grip = new Border
        {
            Width = 18,
            Height = 18,
            Background = Palette.ToastBg,
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4, 0, 4, 0),
            Cursor = new Cursor(StandardCursorType.BottomRightCorner),
            Child = new TextBlock
            {
                Text = "◢",
                Foreground = Palette.AccentBrush,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        ToolTip.SetTip(grip, "Drag to resize the layout");
        return grip;
    }

    private void SetExpanded(bool expanded)
    {
        if (_panel.IsVisible == expanded) return;
        _panel.IsVisible = expanded;
        // The open panel floats over everything (even the layout's own windows); collapsed, the tab goes back
        // to normal stacking so other apps can cover it — the service re-places it after this.
        Topmost = expanded;
        ExpandedChanged?.Invoke();
    }

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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsInsidePanel(e.Source)) return;   // buttons in the expanded panel handle themselves

        if (e.ClickCount >= 2)
        {
            _pressed = false;
            DoubleClicked?.Invoke();
            return;
        }

        Win32.GetCursorPos(out _pressAt);
        _pressed = true;
        e.Pointer.Capture(this);

        // The grip has nothing to click — every press is a drag.
        if (HandleKind == Kind.Resize) StartDrag();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed || !Win32.GetCursorPos(out var now)) return;

        var dx = now.x - _pressAt.x;
        var dy = now.y - _pressAt.y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) >= DragThreshold) StartDrag();
        if (_dragging) Dragged?.Invoke(dx, dy);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;

        var wasDrag = _dragging;
        Finish(e.Pointer);
        if (!wasDrag && HandleKind == Kind.Chip) SetExpanded(!IsExpanded);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Finish(null);
    }

    private void StartDrag()
    {
        _dragging = true;
        SetExpanded(false);
        DragStarted?.Invoke();
    }

    private void Finish(IPointer? pointer)
    {
        _pressed = false;
        pointer?.Capture(null);
        if (!_dragging) return;
        _dragging = false;
        DragEnded?.Invoke();
    }

    private bool IsInsidePanel(object? source)
    {
        for (var v = source as StyledElement; v is not null; v = v.Parent)
        {
            if (ReferenceEquals(v, _panel)) return true;
            if (v is Button) return true;
        }
        return false;
    }
}
