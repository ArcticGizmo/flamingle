using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Views.Design;

/// <summary>
/// Design mode's floating context panel: the saved-flock editor, focused on whatever is selected on screen. Unlike
/// the layout panel it takes the keyboard (arguments and names are typed here), so it's an ordinary activatable
/// window — topmost, out of the taskbar and Alt+Tab. Drag its top strip to move it out of the way.
/// </summary>
internal sealed class DesignPanelWindow : Window
{
    public const double PanelWidth = 440;

    private readonly ScrollViewer _scroll;

    public DesignPanelWindow()
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        Width = PanelWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var grip = new Border
        {
            Height = 14, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new Border
            {
                Width = 36, Height = 4, CornerRadius = new CornerRadius(2), Background = Palette.Line,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
        };

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(18, 0, 18, 18),
        };

        Content = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.AccentBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), ClipToBounds = true,
            Child = new DockPanel { Children = { grip, _scroll } },
        };
        DockPanel.SetDock(grip, Dock.Top);
    }

    /// <summary>Esc was pressed in the panel.</summary>
    public event Action? EscapePressed;

    public Control? Body
    {
        get => _scroll.Content as Control;
        set => _scroll.Content = value;
    }

    /// <summary>Keep the panel inside <paramref name="workArea"/> (physical px) and no taller than it.</summary>
    public void FitTo(PixelRect workArea, double scale) => MaxHeight = Math.Max(240, workArea.Height / scale - 48);

    /// <summary>The panel's size in physical px on a monitor at <paramref name="scale"/>.</summary>
    public (int Width, int Height) PixelSizeAt(double scale)
    {
        var height = Bounds.Height > 0 ? Bounds.Height : 420;
        return ((int)Math.Ceiling(PanelWidth * scale), (int)Math.Ceiling(height * scale));
    }

    /// <summary>Move to <paramref name="x"/>, <paramref name="y"/> (physical px) without changing its Z-order.</summary>
    public void MoveTo(int x, int y)
    {
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            Position = new PixelPoint(x, y);
            return;
        }
        Win32.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            Win32.SWP_NOZORDER | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            EscapePressed?.Invoke();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        // Out of Alt+Tab; it still takes the keyboard.
        var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_TOOLWINDOW));
    }
}
