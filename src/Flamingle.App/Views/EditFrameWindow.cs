using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Views;

/// <summary>
/// A layout in edit mode: an accent outline over the whole layout with a faint tint, and the hint of how to get
/// out. Drag anywhere on it to move the layout (and every window in it); double-click to fill the monitor. Takes
/// the mouse (so the windows beneath don't), never activation; topmost while it's up. Drag offsets are physical
/// pixels, read straight from the cursor.
/// </summary>
internal sealed class EditFrameWindow : Window
{
    private const int DragThreshold = 4;

    private Win32.POINT _pressAt;
    private bool _pressed;
    private bool _dragging;

    public EditFrameWindow(string hint)
    {
        WindowDecorations = WindowDecorations.None;
        Background = new SolidColorBrush(Palette.Accent, 0.07);   // not transparent: the whole frame takes the mouse
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.SizeAll);

        Content = new Grid
        {
            Children =
            {
                new Rectangle
                {
                    Stroke = Palette.AccentBrush, StrokeThickness = 3, RadiusX = 8, RadiusY = 8,
                    StrokeDashArray = [4, 2],
                },
                new Border
                {
                    Background = Palette.ToastBg,
                    BorderBrush = Palette.AccentBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 6),
                    Margin = new Thickness(0, 0, 0, 20),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new TextBlock { Text = hint, Foreground = Palette.Fg, FontSize = 12 },
                },
            },
        };
    }

    public event Action? DragStarted;

    /// <summary>Total cursor offset since the drag started, in physical pixels.</summary>
    public event Action<int, int>? Dragged;

    public event Action? DragEnded;

    public event Action? DoubleClicked;

    /// <summary>Cover <paramref name="rect"/> (physical px) on a monitor at <paramref name="scale"/>.</summary>
    public void Place(PixelRect rect, double scale) => OverlayPlacement.Place(this, rect, scale);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount >= 2)
        {
            _pressed = false;
            DoubleClicked?.Invoke();
            return;
        }
        Win32.GetCursorPos(out _pressAt);
        _pressed = true;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed || !Win32.GetCursorPos(out var now)) return;
        var dx = now.x - _pressAt.x;
        var dy = now.y - _pressAt.y;
        if (!_dragging && Math.Abs(dx) + Math.Abs(dy) >= DragThreshold)
        {
            _dragging = true;
            DragStarted?.Invoke();
        }
        if (_dragging) Dragged?.Invoke(dx, dy);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Finish(e.Pointer);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        Finish(null);
    }

    private void Finish(IPointer? pointer)
    {
        _pressed = false;
        pointer?.Capture(null);
        if (!_dragging) return;
        _dragging = false;
        DragEnded?.Invoke();
    }
}
