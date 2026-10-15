using Flamingle.Core.Geometry;

namespace Flamingle.Core.Saved;

/// <summary>An open top-level window as relaunch sees it: who owns it and what it's called.</summary>
/// <param name="ExePath">The full exe path of the app that owns it (for a Store app, the app's own process).</param>
/// <param name="AppUserModelId">The window's AppUserModelID, or its package's app id; null if neither.</param>
public sealed record OpenWindow(long Handle, string? ExePath, string? AppUserModelId, string ClassName, string Title);

/// <summary>
/// Decides which open window is a planned window. Matching is by app — the exe or the Store app id, never the process
/// id, since single-instance apps hand new windows to a process that's already running — then by the title to
/// recognise it by, if the recipe has one.
/// </summary>
public static class WindowMatcher
{
    /// <summary>Is <paramref name="w"/> a window of the planned window's app? Either the app its recipe starts, or
    /// the app it was captured from (an edited recipe may start it through a launcher, e.g. <c>wt.exe</c>).</summary>
    public static bool IsApp(PlannedWindow p, OpenWindow w)
    {
        var target = p.Recipe.Kind switch
        {
            LaunchKind.Program => Same(w.ExePath, p.Target),
            LaunchKind.StoreApp => Same(w.AppUserModelId, p.Target),
            _ => false,
        };
        var saved = p.Window;
        var captured = saved.Packaged
            ? Same(w.AppUserModelId, saved.AppUserModelId)
            : Same(w.ExePath, saved.ExePath) || (saved.AppUserModelId is not null && Same(w.AppUserModelId, saved.AppUserModelId));
        return target || captured;
    }

    public static bool Matches(PlannedWindow p, OpenWindow w)
        => IsApp(p, w)
           && (string.IsNullOrEmpty(p.TitleContains) || w.Title.Contains(p.TitleContains, StringComparison.OrdinalIgnoreCase));

    /// <summary>The best match among <paramref name="candidates"/> (given topmost first): one with the captured
    /// window's class, then one with its title, then the topmost.</summary>
    public static OpenWindow? Best(PlannedWindow p, IEnumerable<OpenWindow> candidates)
        => candidates
            .Where(w => Matches(p, w))
            .Select((w, i) => (w, i))
            .OrderByDescending(x => x.w.ClassName == p.Window.ClassName)
            .ThenByDescending(x => string.Equals(x.w.Title, p.Window.Title, StringComparison.OrdinalIgnoreCase))
            .ThenBy(x => x.i)
            .Select(x => x.w)
            .FirstOrDefault();

    private static bool Same(string? a, string? b)
        => !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Waits for a newly started app's window to settle before it's placed: its title and size unchanged for
/// <see cref="Quiet"/> and big enough to be the real window — so a splash screen (or a window still being laid out)
/// isn't taken for it.
/// </summary>
public sealed class WindowSettler(TimeProvider? time = null)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(600);
    public const int MinWidth = 200;
    public const int MinHeight = 120;

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<long, (string Title, PixelRect Rect, DateTimeOffset Since)> _seen = [];

    /// <summary>Note how a window looks now; true once it has settled.</summary>
    public bool Observe(long handle, string title, PixelRect rect)
    {
        var now = _time.GetUtcNow();
        if (!_seen.TryGetValue(handle, out var was) || was.Title != title || was.Rect != rect)
        {
            _seen[handle] = (title, rect, now);
            return false;
        }
        return now - was.Since >= Quiet && title.Length > 0 && rect.Width >= MinWidth && rect.Height >= MinHeight;
    }
}

/// <summary>Puts a saved placement onto today's monitors.</summary>
public static class PlacementMapper
{
    /// <summary>The monitor a saved window goes to: the same device, else one the same size, else the primary.</summary>
    public static MonitorInfo Monitor(string device, PixelRect savedWorkArea, IReadOnlyList<MonitorInfo> monitors)
        => monitors.FirstOrDefault(m => m.DeviceName == device)
           ?? monitors.FirstOrDefault(m => m.WorkArea.Width == savedWorkArea.Width && m.WorkArea.Height == savedWorkArea.Height)
           ?? monitors.FirstOrDefault(m => m.IsPrimary)
           ?? monitors[0];

    /// <summary>
    /// Carry a saved window rect from the work area it was saved in to <paramref name="to"/>: as saved when nothing
    /// changed, else the same place and size relative to the work area; then kept on screen (never bigger than the
    /// work area, slid inside it).
    /// </summary>
    public static PixelRect Rect(PixelRect rect, PixelRect from, PixelRect to)
    {
        if (from == to) return rect;
        if (from.Width > 0 && from.Height > 0)
        {
            double Sx(int x) => to.X + (x - from.X) * (double)to.Width / from.Width;
            double Sy(int y) => to.Y + (y - from.Y) * (double)to.Height / from.Height;
            rect = PixelRect.FromEdges(
                (int)Math.Round(Sx(rect.X)), (int)Math.Round(Sy(rect.Y)),
                (int)Math.Round(Sx(rect.Right)), (int)Math.Round(Sy(rect.Bottom)));
        }
        var w = Math.Min(rect.Width, to.Width);
        var h = Math.Min(rect.Height, to.Height);
        return new PixelRect(Math.Clamp(rect.X, to.X, to.Right - w), Math.Clamp(rect.Y, to.Y, to.Bottom - h), w, h);
    }
}
