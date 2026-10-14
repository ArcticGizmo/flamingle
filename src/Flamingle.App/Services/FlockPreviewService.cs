using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.App.Views;
using Flamingle.Core.Grouping;
using Flamingle.Core.Settings;
using Flamingle.Core.Taskbar;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Services;

/// <summary>
/// The flock taskbar button's preview and hover outline.
///
/// <para><b>Preview.</b> When the taskbar asks a flock's button for its preview, capture the flock's windows
/// (off the UI thread — it's a cross-process call), lay the pictures out as the windows sit on screen and hand
/// DWM the result. Minimised windows can't be captured, so each flock's windows are also captured just before
/// Flamingle minimises them, and that picture stands in until they're back.</para>
///
/// <para><b>Hover.</b> Windows doesn't say when a taskbar button is hovered — but it asks for the preview
/// then. So a preview request with the cursor on the taskbar starts a "hover": the flock's windows get an
/// outline. It ends once the cursor has left the button (and the preview popup) for a moment; the preview is
/// then invalidated so the next hover asks — and outlines — again.</para>
/// </summary>
internal sealed class FlockPreviewService : IDisposable
{
    // How long to wait for captures before serving a preview with what we have.
    private static readonly TimeSpan ServeCaptureBudget = TimeSpan.FromMilliseconds(400);

    // How long a minimise may be held up while the windows are captured (blocks the UI thread — keep it short).
    private static readonly TimeSpan MinimiseCaptureBudget = TimeSpan.FromMilliseconds(150);

    // The cursor counts as still on the button within this distance (DIP) of where the hover started.
    private const double ButtonReach = 48;

    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(250);

    // A preview served without a hover (Alt+Tab, keyboard focus on the taskbar) is invalidated after this,
    // so the next real hover asks again.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);

    private static readonly IBrush TileEdge = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<long, Picture> _pictures = [];
    private readonly Dictionary<IntPtr, int> _requests = [];
    private readonly DispatcherTimer _hoverTimer;
    private Hover? _hover;

    private sealed record Picture(PixelRect Bounds, Bitmap Image);

    private sealed class Hover(Guid flockId, IntPtr proxy, int startX)
    {
        public Guid FlockId { get; } = flockId;
        public IntPtr Proxy { get; } = proxy;
        public int StartX { get; set; } = startX;
        public DateTime? AwaySince { get; set; }
        public List<FlockOutlineWindow> Outlines { get; } = [];
    }

    public FlockPreviewService(Func<AppSettings> settings)
    {
        _settings = settings;
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTimer.Tick += (_, _) => HoverTick();
    }

    // ---- Captures ----

    /// <summary>Picture the flock's on-screen windows before they go down. Waits briefly (bounded) for them.</summary>
    public void CaptureBeforeMinimise(Flock group)
    {
        var tasks = group.Members
            .Select(m => (IntPtr)m.Handle)
            .Where(h => !Win32.IsIconic(h))
            .Select(h => Task.Run(() => (Handle: (long)h, Shot: WindowCapture.Capture(h))))
            .ToArray();
        Task.WaitAll(tasks, MinimiseCaptureBudget);
        Keep(tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result));
    }

    private void Keep(IEnumerable<(long Handle, CapturedWindow? Shot)> shots)
    {
        foreach (var (handle, shot) in shots)
        {
            if (shot is null) continue;
            if (_pictures.Remove(handle, out var old)) old.Image.Dispose();
            _pictures[handle] = new Picture(shot.Bounds, Bitmaps.FromPixels(shot.Width, shot.Height, shot.Pixels));
        }
    }

    private void Prune()
    {
        foreach (var dead in _pictures.Keys.Where(h => !Win32.IsWindow((IntPtr)h)).ToList())
        {
            if (_pictures.Remove(dead, out var old)) old.Image.Dispose();
        }
    }

    // ---- Preview ----

    /// <summary>The taskbar asked <paramref name="proxy"/> (the flock's button) for a preview up to the given size.</summary>
    public async void Serve(Flock group, IntPtr proxy, int maxWidth, int maxHeight)
    {
        var request = _requests[proxy] = _requests.GetValueOrDefault(proxy) + 1;
        HoverRequested(group, proxy);

        var tasks = group.Members
            .Select(m => (IntPtr)m.Handle)
            .Where(h => Win32.IsWindow(h) && !Win32.IsIconic(h))
            .Select(h => Task.Run(() => (Handle: (long)h, Shot: WindowCapture.Capture(h))))
            .ToArray();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(ServeCaptureBudget));
        Keep(tasks.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result));

        if (_requests.GetValueOrDefault(proxy) != request) return;   // a newer request has taken over
        Prune();
        if (Compose(group, maxWidth, maxHeight) is { } preview)
            TaskbarThumbnails.SetThumbnail(proxy, preview.Width, preview.Height, preview.Pixels);
    }

    /// <summary>The flock's windows, bottom of the Z-order first, with where each is (or will come back to).</summary>
    private List<(long Handle, PixelRect Rect, bool Minimised)> Arrangement(Flock group)
    {
        var order = ZOrder.Sort(group.Members.Select(m => m.Handle), WindowQuery.ZOrderTopFirst());
        var list = new List<(long, PixelRect, bool)>();
        foreach (var h in order.Reverse())
        {
            var hwnd = (IntPtr)h;
            if (!Win32.IsWindow(hwnd)) continue;
            var minimised = Win32.IsIconic(hwnd);
            var rect = !minimised
                ? WindowQuery.VisibleBounds(hwnd)
                : _pictures.TryGetValue(h, out var p) ? p.Bounds : WindowQuery.RestoredRect(hwnd) ?? default;
            list.Add((h, rect, minimised));
        }
        return list;
    }

    private (int Width, int Height, byte[] Pixels)? Compose(Flock group, int maxWidth, int maxHeight)
    {
        var windows = Arrangement(group);
        var (width, height, tiles) = PreviewLayout.Fit(windows.Select(w => w.Rect).ToList(), maxWidth, maxHeight);
        if (width == 0 || height == 0) return null;

        var canvas = new Canvas { Width = width, Height = height };
        for (var i = 0; i < windows.Count; i++)
        {
            var t = tiles[i];
            if (t.Width <= 0 || t.Height <= 0) continue;

            var handle = windows[i].Handle;
            var tile = new Border
            {
                Width = t.Width,
                Height = t.Height,
                CornerRadius = new CornerRadius(3),
                ClipToBounds = true,
                Background = Palette.Row,
                BorderBrush = TileEdge,
                BorderThickness = new Thickness(1),
                Child = _pictures.TryGetValue(handle, out var picture)
                    ? new Image { Source = picture.Image, Stretch = Stretch.Fill }
                    : Placeholder((IntPtr)handle, t.Height),
            };
            Canvas.SetLeft(tile, t.X);
            Canvas.SetTop(tile, t.Y);
            canvas.Children.Add(tile);
        }

        return (width, height, Bitmaps.Render(canvas, width, height));
    }

    /// <summary>A window we have no picture of: its app icon and title.</summary>
    private static Control Placeholder(IntPtr hwnd, int height)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4),
        };
        var iconSize = Math.Clamp(height / 3, 12, 32);
        if (AppIcons.For(hwnd, iconSize) is { } icon)
            panel.Children.Add(new Image { Source = icon, Width = iconSize, Height = iconSize });
        if (height >= 48)
        {
            panel.Children.Add(new TextBlock
            {
                Text = WindowQuery.Title(hwnd),
                Foreground = Palette.Fg,
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        return panel;
    }

    // ---- Hover ----

    private void HoverRequested(Flock group, IntPtr proxy)
    {
        if (!Win32.GetCursorPos(out var cursor) || WindowQuery.ShellSpotAt(cursor.x, cursor.y) != WindowQuery.ShellSpot.Taskbar)
        {
            // Not a hover (Alt+Tab, keyboard): let the preview go stale soon so a real hover asks again.
            if (_hover?.Proxy != proxy)
                DispatcherTimer.RunOnce(() => { if (_hover?.Proxy != proxy) TaskbarThumbnails.Invalidate(proxy); }, StaleAfter);
            return;
        }

        if (_hover is { } current && current.FlockId == group.Id)
        {
            current.StartX = cursor.x;
            current.AwaySince = null;
            return;
        }

        EndHover();
        _hover = new Hover(group.Id, proxy, cursor.x);
        if (_settings().TaskbarHoverOutline) ShowOutlines(group, _hover);
        _hoverTimer.Start();
    }

    private void ShowOutlines(Flock group, Hover hover)
    {
        var monitors = Monitors.All();
        var windows = Arrangement(group);
        for (var i = 0; i < windows.Count; i++)
        {
            var (_, rect, minimised) = windows[i];
            if (rect.Width <= 0 || rect.Height <= 0) continue;
            var centre = (X: rect.X + rect.Width / 2, Y: rect.Y + rect.Height / 2);
            var scale = monitors.FirstOrDefault(m => m.Contains(centre.X, centre.Y))?.Scale ?? 1.0;
            var topmost = i == windows.Count - 1;   // the flock's name goes on its front window
            var outline = new FlockOutlineWindow(rect, scale, minimised, topmost ? group.Name : null);
            hover.Outlines.Add(outline);
            outline.Show();
        }
    }

    private void HoverTick()
    {
        if (_hover is not { } hover) { _hoverTimer.Stop(); return; }
        if (!Win32.GetCursorPos(out var cursor)) return;

        var spot = WindowQuery.ShellSpotAt(cursor.x, cursor.y);
        var scale = Monitors.All().FirstOrDefault(m => m.Contains(cursor.x, cursor.y))?.Scale ?? 1.0;
        var onButton = spot == WindowQuery.ShellSpot.Taskbar && Math.Abs(cursor.x - hover.StartX) <= ButtonReach * scale;
        if (onButton || spot == WindowQuery.ShellSpot.ShellPopup)
        {
            hover.AwaySince = null;
            return;
        }

        var now = DateTime.UtcNow;
        hover.AwaySince ??= now;
        if (now - hover.AwaySince >= HoverGrace) EndHover();
    }

    /// <summary>The flock went away (or its button did) — drop any hover on it.</summary>
    public void Forget(Guid flockId)
    {
        if (_hover?.FlockId == flockId) EndHover();
    }

    private void EndHover()
    {
        if (_hover is not { } hover) return;
        _hover = null;
        _hoverTimer.Stop();
        foreach (var w in hover.Outlines) w.Close();
        TaskbarThumbnails.Invalidate(hover.Proxy);
    }

    public void Dispose()
    {
        EndHover();
        foreach (var p in _pictures.Values) p.Image.Dispose();
        _pictures.Clear();
    }
}
