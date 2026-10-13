using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.App.Views;
using Flamingle.Core.Grouping;
using Flamingle.Core.Settings;

namespace Flamingle.App.Services;

/// <summary>
/// Puts each flock on the taskbar as its own button — titled with the flock's name, its icon a mosaic of
/// the member apps' icons. Clicking the button minimises / restores the whole flock (same rule as the
/// toggle hotkey); "Close window" on it dissolves the flock (the windows themselves stay open).
/// </summary>
internal sealed class TaskbarService : IDisposable
{
    private const int IconSize = 64;

    private readonly FlockRegistry _registry;
    private readonly FlockController _controller;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<Guid, Proxy> _proxies = [];

    private sealed class Proxy(TaskbarProxyWindow window)
    {
        public TaskbarProxyWindow Window { get; } = window;
        public string IconKey { get; set; } = "";
    }

    // Desktop switches cloak / uncloak a burst of windows — check once the burst has passed.
    private static readonly TimeSpan DesktopCheckDelay = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _desktopCheck;

    public TaskbarService(FlockRegistry registry, FlockController controller, WinEventHooks hooks,
        Func<AppSettings> settings)
    {
        _registry = registry;
        _controller = controller;
        _settings = settings;

        _desktopCheck = new DispatcherTimer { Interval = DesktopCheckDelay };
        _desktopCheck.Tick += (_, _) =>
        {
            _desktopCheck.Stop();
            FollowDesktops();
        };
        hooks.ForegroundChanged += _ => ScheduleDesktopCheck();
        hooks.CloakChanged += _ => ScheduleDesktopCheck();
    }

    /// <summary>Bring the buttons in line with the current flocks (and the on/off setting).</summary>
    public void Sync()
    {
        var wanted = _settings().TaskbarButtons
            ? _registry.Groups.ToDictionary(g => g.Id)
            : new Dictionary<Guid, Flock>();

        foreach (var id in _proxies.Keys.Where(id => !wanted.ContainsKey(id)).ToList())
        {
            _proxies[id].Window.CloseForGood();
            _proxies.Remove(id);
        }

        foreach (var group in wanted.Values)
        {
            if (!_proxies.TryGetValue(group.Id, out var proxy))
            {
                var window = new TaskbarProxyWindow(group.Id);
                var id = group.Id;
                window.Clicked += () => { if (_registry.Find(id) is { } g) _controller.Toggle(g); };
                window.CloseRequested += () => { if (_registry.Find(id) is { } g) _registry.Dissolve(g); };
                proxy = new Proxy(window);
                _proxies[group.Id] = proxy;
                window.Show();
            }

            proxy.Window.Title = $"{group.Name} — flock of {group.Members.Count}";

            // Rebuilding the icon means asking every member app for its icon — only when membership changes.
            var key = string.Join(",", group.Members.Select(m => m.Handle));
            if (key != proxy.IconKey)
            {
                proxy.IconKey = key;
                proxy.Window.Icon = Mosaic(group);
            }
        }

        FollowDesktops();
    }

    private void ScheduleDesktopCheck()
    {
        if (_proxies.Count == 0 || !_settings().TaskbarButtonFollowsDesktop) return;
        _desktopCheck.Stop();
        _desktopCheck.Start();
    }

    /// <summary>
    /// Move each flock's button to the virtual desktop its windows are on. The taskbar (by default) only shows
    /// buttons for windows on the current desktop, so a button left behind would be missing where the flock is.
    /// </summary>
    private void FollowDesktops()
    {
        if (!_settings().TaskbarButtonFollowsDesktop) return;

        var focused = _controller.LastFocus;
        foreach (var (id, proxy) in _proxies)
        {
            var hwnd = proxy.Window.Handle;
            if (hwnd == IntPtr.Zero || _registry.Find(id) is not { } group) continue;

            var members = group.Members.Select(m => m.Handle).ToList();
            var desktops = members.Select(h => VirtualDesktops.DesktopOf((IntPtr)h) ?? Guid.Empty).ToList();
            var focusIndex = members.IndexOf(focused);
            Guid? focusedDesktop = focusIndex >= 0 ? desktops[focusIndex] : null;

            if (FlockDesktop.Choose(desktops, focusedDesktop) is not { } target) continue;
            if (VirtualDesktops.DesktopOf(hwnd) != target) VirtualDesktops.MoveTo(hwnd, target);
        }
    }

    /// <summary>Up to four member-app icons tiled into one (distinct apps first).</summary>
    private static WindowIcon Mosaic(Flock group)
    {
        var icons = group.Members
            .DistinctBy(m => m.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Concat(group.Members)
            .DistinctBy(m => m.Handle)
            .Select(m => AppIcons.For((IntPtr)m.Handle, IconSize / 2))
            .OfType<Bitmap>()
            .Take(4)
            .ToList();

        var canvas = new Canvas { Width = IconSize, Height = IconSize };
        if (icons.Count == 0)
        {
            return TrayIconArt.Product();
        }

        if (icons.Count == 1)
        {
            Place(canvas, icons[0], 0, 0, IconSize);
        }
        else
        {
            const int cell = IconSize / 2;
            // Two icons sit side by side, vertically centred; three or four fill a 2×2 grid.
            var offsetY = icons.Count == 2 ? cell / 2 : 0;
            for (var i = 0; i < icons.Count; i++)
                Place(canvas, icons[i], i % 2 * cell, i / 2 * cell + offsetY, cell);
        }

        canvas.Measure(new Size(IconSize, IconSize));
        canvas.Arrange(new Rect(0, 0, IconSize, IconSize));
        var bitmap = new RenderTargetBitmap(new PixelSize(IconSize, IconSize), new Vector(96, 96));
        bitmap.Render(canvas);
        return new WindowIcon(bitmap);
    }

    private static void Place(Canvas canvas, Bitmap icon, double x, double y, double size)
    {
        var image = new Image { Source = icon, Width = size - 2, Height = size - 2, Stretch = Stretch.Uniform };
        Canvas.SetLeft(image, x + 1);
        Canvas.SetTop(image, y + 1);
        canvas.Children.Add(image);
    }

    public void Dispose()
    {
        _desktopCheck.Stop();
        foreach (var p in _proxies.Values) p.Window.CloseForGood();
        _proxies.Clear();
    }
}
