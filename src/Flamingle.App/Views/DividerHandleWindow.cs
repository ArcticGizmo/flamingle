using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;

namespace Flamingle.App.Views;

/// <summary>
/// The grab strip over the gap between two (or more) zones. The whole strip takes the mouse (with a resize
/// cursor); a small pill in the middle shows it's there. Dragging reports the cursor offset in physical
/// pixels; the service moves the divider and re-flows the windows on both sides. Only shown while the user
/// is adjusting splits (from the layout tab), so it's topmost for that short time; never activated.
/// </summary>
internal sealed class DividerHandleWindow : Window
{
    private static readonly IBrush Idle = new SolidColorBrush(Palette.Accent, 0.55);
    private static readonly IBrush Hot = Palette.AccentBrush;

    private readonly Rectangle _pill;
    private Win32.POINT _pressAt;
    private bool _dragging;

    public DividerHandleWindow(bool vertical)
    {
        Vertical = vertical;
        WindowDecorations = WindowDecorations.None;
        // Almost-transparent rather than transparent, so the whole strip is hit-testable.
        Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(vertical ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);

        _pill = new Rectangle
        {
            Width = vertical ? 6 : 56,
            Height = vertical ? 56 : 6,
            RadiusX = 2,
            RadiusY = 2,
            Fill = Idle,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Content = new Grid { Children = { _pill } };
        ToolTip.SetTip(this, "Drag to resize the windows on both sides · double-click to even out the splits");

        PointerEntered += (_, _) => _pill.Fill = Hot;
        PointerExited += (_, _) => { if (!_dragging) _pill.Fill = Idle; };
    }

    public bool Vertical { get; }

    public event Action? DragStarted;

    /// <summary>Total cursor offset since the drag started, in physical pixels.</summary>
    public event Action<int, int>? Dragged;

    public event Action? DragEnded;

    public event Action? DoubleClicked;

    /// <summary>Place the strip over <paramref name="rect"/> (physical px) on a monitor at <paramref name="scale"/>.</summary>
    public void Place(Flamingle.Core.Geometry.PixelRect rect, double scale) => OverlayPlacement.Place(this, rect, scale);

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
            DoubleClicked?.Invoke();
            return;
        }

        Win32.GetCursorPos(out _pressAt);
        _dragging = true;
        _pill.Fill = Hot;
        e.Pointer.Capture(this);
        DragStarted?.Invoke();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || !Win32.GetCursorPos(out var now)) return;
        Dragged?.Invoke(now.x - _pressAt.x, now.y - _pressAt.y);
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
        if (!_dragging) return;
        _dragging = false;
        _pill.Fill = Idle;
        pointer?.Capture(null);
        DragEnded?.Invoke();
    }
}
