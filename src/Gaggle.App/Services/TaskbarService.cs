using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Gaggle.App.Native;
using Gaggle.App.Views;
using Gaggle.Core.Grouping;
using Gaggle.Core.Settings;

namespace Gaggle.App.Services;

/// <summary>
/// Puts each gaggle on the taskbar as its own button — titled with the gaggle's name, its icon a mosaic of
/// the member apps' icons. Clicking the button minimises / restores the whole gaggle (same rule as the
/// toggle hotkey); "Close window" on it dissolves the gaggle (the windows themselves stay open).
/// </summary>
internal sealed class TaskbarService : IDisposable
{
    private const int IconSize = 64;

    private readonly GaggleRegistry _registry;
    private readonly GaggleController _controller;
    private readonly Func<AppSettings> _settings;
    private readonly Dictionary<Guid, Proxy> _proxies = [];

    private sealed class Proxy(TaskbarProxyWindow window)
    {
        public TaskbarProxyWindow Window { get; } = window;
        public string IconKey { get; set; } = "";
    }

    public TaskbarService(GaggleRegistry registry, GaggleController controller, Func<AppSettings> settings)
    {
        _registry = registry;
        _controller = controller;
        _settings = settings;
    }

    /// <summary>Bring the buttons in line with the current gaggles (and the on/off setting).</summary>
    public void Sync()
    {
        var wanted = _settings().TaskbarButtons
            ? _registry.Groups.ToDictionary(g => g.Id)
            : new Dictionary<Guid, GaggleGroup>();

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

            proxy.Window.Title = $"{group.Name} — gaggle of {group.Members.Count}";

            // Rebuilding the icon means asking every member app for its icon — only when membership changes.
            var key = string.Join(",", group.Members.Select(m => m.Handle));
            if (key != proxy.IconKey)
            {
                proxy.IconKey = key;
                proxy.Window.Icon = Mosaic(group);
            }
        }
    }

    /// <summary>Up to four member-app icons tiled into one (distinct apps first).</summary>
    private static WindowIcon Mosaic(GaggleGroup group)
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
            return TrayIconArt.Create(dev: false);
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
        foreach (var p in _proxies.Values) p.Window.CloseForGood();
        _proxies.Clear();
    }
}
