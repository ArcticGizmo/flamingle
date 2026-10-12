using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Gaggle.App.Native;
using Gaggle.Core.Geometry;
using PixelRect = Gaggle.Core.Geometry.PixelRect;

namespace Gaggle.App.Views;

/// <summary>
/// The zones drawn over one monitor's work area while a window is being dragged. Click-through,
/// never activated, topmost — purely a picture; <see cref="Services.ZoneDragService"/> does the hit-testing
/// from the cursor position. Coordinates come in as physical pixels and are mapped to this monitor's DIPs.
/// </summary>
internal sealed class ZoneOverlayWindow : Window
{
    private static readonly IBrush IdleFill = new SolidColorBrush(Color.FromArgb(0x40, 0x0B, 0x12, 0x20));
    private static readonly IBrush IdleStroke = new SolidColorBrush(Palette.Accent, 0.55);
    private static readonly IBrush HotFill = new SolidColorBrush(Palette.Accent, 0.38);
    private static readonly IBrush HotStroke = Palette.AccentBrush;

    private readonly MonitorInfo _monitor;
    private readonly double _scale;
    private readonly List<Border> _tiles = [];
    private readonly HashSet<int> _hot = [];

    public ZoneOverlayWindow(MonitorInfo monitor, IReadOnlyList<PixelRect> zones, string caption)
    {
        _monitor = monitor;
        _scale = monitor.Scale <= 0 ? 1.0 : monitor.Scale;

        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var canvas = new Canvas();
        for (var i = 0; i < zones.Count; i++)
        {
            var local = ToLocal(zones[i]);
            var tile = new Border
            {
                Width = local.Width,
                Height = local.Height,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2),
                Background = IdleFill,
                BorderBrush = IdleStroke,
                Child = new TextBlock
                {
                    Text = (i + 1).ToString(),
                    FontSize = 40,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = new SolidColorBrush(Colors.White, 0.7),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Canvas.SetLeft(tile, local.X);
            Canvas.SetTop(tile, local.Y);
            canvas.Children.Add(tile);
            _tiles.Add(tile);
        }

        var pill = new Border
        {
            Background = Palette.ToastBg,
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 4),
            Child = new TextBlock { Text = caption, Foreground = Palette.Fg, FontSize = 12 },
        };
        Canvas.SetLeft(pill, 16);
        Canvas.SetTop(pill, 16);
        canvas.Children.Add(pill);

        Content = canvas;
    }

    public long MonitorHandle => _monitor.Handle;

    /// <summary>Highlight exactly these zones (empty clears).</summary>
    public void Highlight(IReadOnlyCollection<int> zones)
    {
        if (_hot.SetEquals(zones)) return;
        _hot.Clear();
        _hot.UnionWith(zones);
        for (var i = 0; i < _tiles.Count; i++)
        {
            var hot = _hot.Contains(i);
            _tiles[i].Background = hot ? HotFill : IdleFill;
            _tiles[i].BorderBrush = hot ? HotStroke : IdleStroke;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var area = _monitor.WorkArea;
        Position = new PixelPoint(area.X, area.Y);
        Width = area.Width / _scale;
        Height = area.Height / _scale;

        // Let the drag's mouse input fall straight through to the desktop.
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex
                | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
        }
    }

    private Rect ToLocal(PixelRect r) => new(
        (r.X - _monitor.WorkArea.X) / _scale,
        (r.Y - _monitor.WorkArea.Y) / _scale,
        r.Width / _scale,
        r.Height / _scale);
}
