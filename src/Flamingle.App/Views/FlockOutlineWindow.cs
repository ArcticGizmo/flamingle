using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Views;

/// <summary>
/// An accent outline drawn around one of a flock's windows while its taskbar button is hovered — so you can see
/// which windows the button stands for. Dashed for a minimised window (outlining where it will come back to).
/// Click-through, never activated, topmost; purely a picture.
/// </summary>
internal sealed class FlockOutlineWindow : Window
{
    private const double Stroke = 3;

    private readonly PixelRect _rect;
    private readonly double _scale;

    /// <param name="rect">The window's visible frame, physical px.</param>
    /// <param name="scale">The DPI scale of the monitor it's on.</param>
    /// <param name="label">The flock's name, shown in a pill in the corner (null for none).</param>
    public FlockOutlineWindow(PixelRect rect, double scale, bool minimised, string? label)
    {
        _rect = rect;
        _scale = scale <= 0 ? 1.0 : scale;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var outline = new Rectangle
        {
            Stroke = Palette.AccentBrush,
            StrokeThickness = Stroke,
            Fill = new SolidColorBrush(Palette.Accent, minimised ? 0.06 : 0.12),
            RadiusX = 8,
            RadiusY = 8,
            StrokeDashArray = minimised ? [3, 2] : null,
        };
        var grid = new Grid { Children = { outline } };

        if (label is not null)
        {
            grid.Children.Add(new Border
            {
                Background = Palette.ToastBg,
                BorderBrush = Palette.AccentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4),
                Margin = new Thickness(12),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = minimised ? $"{label} (minimised)" : label,
                    Foreground = Palette.Fg,
                    FontSize = 12,
                },
            });
        }
        Content = grid;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Position = new PixelPoint(_rect.X, _rect.Y);
        Width = Math.Max(1, _rect.Width / _scale);
        Height = Math.Max(1, _rect.Height / _scale);

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex
                | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
        }
    }
}
