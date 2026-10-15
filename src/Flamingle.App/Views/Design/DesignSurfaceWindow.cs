using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;
using Flamingle.App.Views.Settings;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Views.Design;

/// <summary>A window in a design-mode tile (or on the floating shelf).</summary>
/// <param name="Index">The saved window's index in the flock.</param>
/// <param name="Detail">One line under the app: what it starts with.</param>
internal sealed record DesignTileWindow(int Index, string App, string Detail, bool Selected, bool Problem);

/// <summary>A region drawn on a monitor: its outline (physical px), its zones (1-based label) and its windows.</summary>
internal sealed record DesignTile(int Region, PixelRect Bounds, string Zones, IReadOnlyList<DesignTileWindow> Windows, bool Selected);

/// <summary>
/// Design mode on one monitor: the work area dimmed, with the flock's layout drawn over it as tiles — one per region
/// — showing what starts in each. Click a tile to select it (an empty one asks what should start there), drag a
/// window's tile onto another zone to move it, click the monitor's chip to change its layout. Takes the mouse,
/// never activation; topmost while it's up. Everything it reports is in physical pixels or indices — the design
/// service decides what they mean.
/// </summary>
internal sealed class DesignSurfaceWindow : Window
{
    private const int DragThreshold = 6;

    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#0B0F14"), 0.55);
    private static readonly IBrush EmptyFill = new SolidColorBrush(Palette.Accent, 0.05);
    private static readonly IBrush EmptyHover = new SolidColorBrush(Palette.Accent, 0.14);
    private static readonly IBrush FilledFill = new SolidColorBrush(Color.Parse("#1A2129"), 0.92);
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.Parse("#12303D"), 0.95);
    private static readonly IBrush DropFill = new SolidColorBrush(Palette.Accent, 0.30);
    private static readonly IBrush Outline = new SolidColorBrush(Palette.Accent, 0.55);

    private readonly PixelRect _workArea;
    private readonly FractionPanel _tiles = new();
    private readonly StackPanel _shelf = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private readonly Border _shelfBox;
    private readonly TextBlock _chipText = new() { FontSize = 13, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _banner;
    private readonly TextBlock _bannerText = new() { FontSize = 13, Foreground = Palette.Fg };
    private readonly Dictionary<int, Border> _tileBorders = [];
    private IReadOnlyList<DesignTile> _shown = [];
    private int? _dropRegion;

    // A press on a window (dragged once it moves far enough) or on a tile / the background.
    private int? _pressWindow;
    private int? _pressRegion;
    private Win32.POINT _pressAt;
    private bool _pressed;
    private bool _dragging;

    public DesignSurfaceWindow(PixelRect workArea, double scale)
    {
        _workArea = workArea;
        WindowDecorations = WindowDecorations.None;
        Background = Dim;   // not transparent: the whole screen takes the mouse
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var chip = new Border
        {
            Background = Palette.ToastBg, BorderBrush = Palette.AccentBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16), Padding = new Thickness(14, 6), Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    _chipText,
                    new TextBlock { Text = SettingsUi.Glyphs.ChevronDown, FontFamily = SettingsUi.IconFont, FontSize = 10, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        chip.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            ScreenClicked?.Invoke();
        };

        _shelfBox = new Border
        {
            Background = Palette.ToastBg, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8), Margin = new Thickness(0, 0, 0, 18),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Not in a zone", FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center },
                    _shelf,
                },
            },
            IsVisible = false,
        };

        _banner = new Border
        {
            Background = Palette.ToastBg, BorderBrush = Palette.AccentBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8), Margin = new Thickness(0, 60, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Child = _bannerText, IsVisible = false,
        };

        Content = new Grid { Children = { _tiles, chip, _shelfBox, _banner } };
        OverlayPlacement.Place(this, workArea, scale);
    }

    /// <summary>An empty tile (or the empty part of one) was clicked: its region index.</summary>
    public event Action<int>? RegionClicked;

    /// <summary>A window was clicked (in a tile or on the shelf).</summary>
    public event Action<int>? WindowClicked;

    /// <summary>The monitor's chip (name · layout) was clicked.</summary>
    public event Action? ScreenClicked;

    /// <summary>Somewhere with nothing on it was clicked.</summary>
    public event Action? BackgroundClicked;

    /// <summary>A window is being dragged (its index); the cursor says where.</summary>
    public event Action<int>? WindowDragged;

    /// <summary>A dragged window was let go (its index; Ctrl held = span).</summary>
    public event Action<int, bool>? WindowDropped;

    public PixelRect WorkArea => _workArea;

    /// <summary>Draw <paramref name="tiles"/> and the shelf of windows not in a zone.</summary>
    public void Show(string chip, IReadOnlyList<DesignTile> tiles, IReadOnlyList<DesignTileWindow> floating)
    {
        _chipText.Text = chip;
        _shown = tiles;
        _tiles.Children.Clear();
        _tiles.Fractions.Clear();
        _tileBorders.Clear();
        foreach (var t in tiles)
        {
            var border = BuildTile(t);
            _tileBorders[t.Region] = border;
            _tiles.Children.Add(border);
            _tiles.Fractions.Add(new Rect(
                (t.Bounds.X - _workArea.X) / (double)_workArea.Width, (t.Bounds.Y - _workArea.Y) / (double)_workArea.Height,
                t.Bounds.Width / (double)_workArea.Width, t.Bounds.Height / (double)_workArea.Height));
        }
        _tiles.InvalidateArrange();
        RestyleDrop();

        _shelf.Children.Clear();
        foreach (var f in floating) _shelf.Children.Add(ShelfChip(f));
        _shelfBox.IsVisible = floating.Count > 0;
    }

    /// <summary>Highlight the region a drag would drop into (null: none here).</summary>
    public void SetDropTarget(int? region)
    {
        if (_dropRegion == region) return;
        _dropRegion = region;
        RestyleDrop();
    }

    /// <summary>Let the mouse through to the windows beneath (while the user drags a real window in), with a
    /// banner saying what's going on; or take it back.</summary>
    public void SetPassThrough(bool on, string? banner = null)
    {
        _banner.IsVisible = on && banner is not null;
        _bannerText.Text = banner ?? "";
        Background = on ? new SolidColorBrush(Color.Parse("#0B0F14"), 0.25) : Dim;
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        ex = on ? ex | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED : ex & ~(Win32.WS_EX_TRANSPARENT | Win32.WS_EX_LAYERED);
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void RestyleDrop()
    {
        foreach (var (region, border) in _tileBorders)
        {
            var t = _shown.First(x => x.Region == region);
            border.Background = region == _dropRegion ? DropFill : t.Selected ? SelectedFill : t.Windows.Count == 0 ? EmptyFill : FilledFill;
        }
    }

    private Border BuildTile(DesignTile t)
    {
        var empty = t.Windows.Count == 0;
        var content = new Grid();
        content.Children.Add(new TextBlock
        {
            Text = t.Zones, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Outline,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, 12, 0),
        });

        if (empty)
        {
            content.Children.Add(new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "+", FontSize = 40, FontWeight = FontWeight.Light, Foreground = Outline, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Choose what starts here", FontSize = 13, Foreground = Palette.Muted, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        }
        else
        {
            var rows = new StackPanel { Spacing = 6, Margin = new Thickness(14, 14, 40, 14), VerticalAlignment = VerticalAlignment.Top };
            foreach (var w in t.Windows) rows.Children.Add(WindowRow(w));
            content.Children.Add(rows);
        }

        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderBrush = t.Selected ? Palette.AccentBrush : Outline,
            BorderThickness = new Thickness(t.Selected ? 3 : 1.5),
            Margin = new Thickness(2),
            Cursor = new Cursor(empty ? StandardCursorType.Hand : StandardCursorType.SizeAll),
            Child = content,
        };
        if (empty)
        {
            border.PointerEntered += (_, _) => { if (!t.Selected && _dropRegion != t.Region) border.Background = EmptyHover; };
            border.PointerExited += (_, _) => RestyleDrop();
        }
        border.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            // The tile itself: an empty one asks what starts there; a filled one is its (first) window.
            Press(e, empty ? null : t.Windows[0].Index, t.Region);
        };
        return border;
    }

    private Control WindowRow(DesignTileWindow w)
    {
        var text = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = w.App, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = w.Detail, FontSize = 12, Foreground = w.Problem ? SettingsUi.WarnBrush : Palette.Muted,
            TextTrimming = TextTrimming.CharacterEllipsis, FontFamily = new FontFamily("Cascadia Mono, Consolas, Courier New"),
        });
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(SettingsUi.Tile(w.App.Length > 0 ? w.App[..1].ToUpperInvariant() : "?", w.Index));
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var box = new Border
        {
            Background = w.Selected ? new SolidColorBrush(Palette.Accent, 0.16) : Brushes.Transparent,
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Child = row,
        };
        box.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Press(e, w.Index, null);
        };
        return box;
    }

    private Control ShelfChip(DesignTileWindow w)
    {
        var chip = new Border
        {
            Background = w.Selected ? SelectedFill : Palette.Row,
            BorderBrush = w.Selected ? Palette.AccentBrush : Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 5), Cursor = new Cursor(StandardCursorType.SizeAll),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = w.App, FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title },
                    new TextBlock { Text = w.Detail, FontSize = 11, Foreground = Palette.Muted },
                },
            },
        };
        chip.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Press(e, w.Index, null);
        };
        return chip;
    }

    private void Press(PointerPressedEventArgs e, int? window, int? region)
    {
        Win32.GetCursorPos(out _pressAt);
        _pressWindow = window;
        _pressRegion = region;
        _pressed = true;
        _dragging = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Press(e, null, null);   // the dimmed background
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed || _pressWindow is not { } index || !Win32.GetCursorPos(out var now)) return;
        if (!_dragging && Math.Abs(now.x - _pressAt.x) + Math.Abs(now.y - _pressAt.y) >= DragThreshold) _dragging = true;
        if (_dragging) WindowDragged?.Invoke(index);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);

        if (_dragging && _pressWindow is { } dragged)
        {
            _dragging = false;
            WindowDropped?.Invoke(dragged, e.KeyModifiers.HasFlag(KeyModifiers.Control));
            return;
        }
        if (_pressWindow is { } window) WindowClicked?.Invoke(window);
        else if (_pressRegion is { } region) RegionClicked?.Invoke(region);
        else BackgroundClicked?.Invoke();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!_dragging || _pressWindow is not { } index) return;
        _pressed = _dragging = false;
        WindowDropped?.Invoke(index, false);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW));
    }

    /// <summary>Lays its children out at fractions of its own size, so tiles follow the monitor whatever its DPI.</summary>
    private sealed class FractionPanel : Panel
    {
        public List<Rect> Fractions { get; } = [];

        protected override Size MeasureOverride(Size availableSize)
        {
            for (var i = 0; i < Children.Count && i < Fractions.Count; i++)
            {
                var f = Fractions[i];
                Children[i].Measure(new Size(f.Width * availableSize.Width, f.Height * availableSize.Height));
            }
            return default;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            for (var i = 0; i < Children.Count && i < Fractions.Count; i++)
            {
                var f = Fractions[i];
                Children[i].Arrange(new Rect(f.X * finalSize.Width, f.Y * finalSize.Height, f.Width * finalSize.Width, f.Height * finalSize.Height));
            }
            return finalSize;
        }
    }
}
