using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;
using Flamingle.Core.Geometry;
using Flamingle.Core.Zones;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Views;

/// <summary>
/// The strip of layouts that drops down when a window is dragged to the top of a monitor — Flamingle's take on
/// Windows' snap bar. Each layout is drawn small, one tile per zone; the monitor's current layout comes first.
/// Purely a picture (click-through, never activated): the window drag owns the mouse, so
/// <see cref="Services.ZoneDragService"/> hit-tests the cursor against <see cref="HitTest"/> and drives
/// <see cref="Highlight"/>.
/// </summary>
internal sealed class SnapStripWindow : Window
{
    private const double TileWidth = 64;
    private const double TileHeight = 40;
    private const double Gap = 2;

    // Gap between the top of the work area and the strip (DIP) — clear of the very top edge, where Windows
    // offers to maximise.
    public const double TopOffset = 10;

    private static readonly IBrush IdleFill = new SolidColorBrush(Palette.Accent, 0.22);
    private static readonly IBrush IdleStroke = new SolidColorBrush(Palette.Accent, 0.55);
    private static readonly IBrush HotFill = new SolidColorBrush(Palette.Accent, 0.85);

    private readonly MonitorInfo _monitor;
    private readonly List<(string LayoutId, int Zone, Border Tile)> _tiles = [];
    private readonly TextBlock _caption = new() { Foreground = Palette.Muted, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center };

    public SnapStripWindow(MonitorInfo monitor, string currentLayoutId)
    {
        _monitor = monitor;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var layouts = new WrapPanel { MaxWidth = 7 * (TileWidth + 14), HorizontalAlignment = HorizontalAlignment.Center };
        var current = ZoneLayout.ById(currentLayoutId);
        foreach (var layout in ZoneLayout.Templates.OrderBy(l => l.Id == current.Id ? 0 : 1))
            layouts.Children.Add(Card(layout, layout.Id == current.Id));

        _caption.Text = "Drop on a zone · Ctrl to span zones";
        Content = new Border
        {
            Background = Palette.ToastBg,
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 8, 8, 6),
            Child = new StackPanel { Spacing = 4, Children = { layouts, _caption } },
        };
    }

    private Control Card(ZoneLayout layout, bool current)
    {
        var canvas = new Canvas { Width = TileWidth, Height = TileHeight };
        for (var i = 0; i < layout.Zones.Count; i++)
        {
            var z = layout.Zones[i];
            var tile = new Border
            {
                Width = Math.Max(1, z.Width * TileWidth - Gap),
                Height = Math.Max(1, z.Height * TileHeight - Gap),
                CornerRadius = new CornerRadius(2),
                Background = IdleFill,
                BorderBrush = IdleStroke,
                BorderThickness = new Thickness(1),
            };
            Canvas.SetLeft(tile, z.X * TileWidth + Gap / 2);
            Canvas.SetTop(tile, z.Y * TileHeight + Gap / 2);
            canvas.Children.Add(tile);
            _tiles.Add((layout.Id, i, tile));
        }

        var card = new Border
        {
            Padding = new Thickness(4),
            Margin = new Thickness(3),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = current ? Palette.AccentBrush : Brushes.Transparent,
            Background = current ? Palette.ButtonBg : Brushes.Transparent,
            Child = canvas,
        };
        ToolTip.SetTip(card, layout.Name);
        return card;
    }

    /// <summary>Where the strip is on screen (physical px).</summary>
    public PixelRect ScreenBounds
    {
        get
        {
            var size = ClientSize * _monitor.Scale;
            return new PixelRect(Position.X, Position.Y, (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height));
        }
    }

    /// <summary>The layout and zone under a screen point (physical px), if any.</summary>
    public (string LayoutId, int Zone)? HitTest(int x, int y)
    {
        var local = new Point((x - Position.X) / _monitor.Scale, (y - Position.Y) / _monitor.Scale);
        foreach (var (layoutId, zone, tile) in _tiles)
        {
            if (tile.TranslatePoint(default, this) is not { } origin) continue;
            if (new Rect(origin, tile.Bounds.Size).Contains(local)) return (layoutId, zone);
        }
        return null;
    }

    /// <summary>Light up these zones of one layout (null clears), and name it in the caption.</summary>
    public void Highlight(string? layoutId, IReadOnlyCollection<int> zones)
    {
        foreach (var (id, zone, tile) in _tiles)
            tile.Background = id == layoutId && zones.Contains(zone) ? HotFill : IdleFill;
        _caption.Text = layoutId is null
            ? "Drop on a zone · Ctrl to span zones"
            : ZoneLayout.ById(layoutId).Name;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Centred along the top of the monitor's work area, just below the edge.
        if (Content is Control content)
        {
            content.Measure(Size.Infinity);
            var w = (int)Math.Ceiling(content.DesiredSize.Width * _monitor.Scale);
            var area = _monitor.WorkArea;
            Position = new PixelPoint(area.X + (area.Width - w) / 2, area.Y + (int)(TopOffset * _monitor.Scale));
        }

        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd != IntPtr.Zero)
        {
            var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex
                | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
        }
    }
}
