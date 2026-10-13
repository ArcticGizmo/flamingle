using Flamingle.Core.Geometry;

namespace Flamingle.App.Native;

/// <summary>
/// Puts windows where their <em>visible</em> frame should be. Windows 10/11 frames carry invisible resize
/// borders, so the rect handed to <c>SetWindowPos</c> is the visible target grown by each window's own
/// border widths (its <see cref="Margins"/>).
/// </summary>
internal static class WindowPlacer
{
    private const int Tolerance = 2;

    /// <summary>How far the window rect extends past the visible frame on each side.</summary>
    public readonly record struct Margins(int Left, int Top, int Right, int Bottom)
    {
        public PixelRect Grow(PixelRect visible) => PixelRect.FromEdges(
            visible.X - Left, visible.Y - Top, visible.Right + Right, visible.Bottom + Bottom);
    }

    public static Margins MarginsOf(IntPtr hwnd)
    {
        var window = WindowQuery.WindowRect(hwnd);
        var visible = WindowQuery.Snapshot(hwnd).Bounds;
        if (window is not { } w || visible.Width <= 0) return default;
        return new Margins(visible.X - w.X, visible.Y - w.Y, w.Right - visible.Right, w.Bottom - visible.Bottom);
    }

    /// <summary>Fit one window's visible frame to <paramref name="target"/> (async — never blocks on the app).</summary>
    public static void Fit(IntPtr hwnd, PixelRect target)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd)) return;
        if (Win32.IsZoomed(hwnd)) Win32.ShowWindowAsync(hwnd, Win32.SW_RESTORE);

        var rect = MarginsOf(hwnd).Grow(target);
        if (WindowQuery.WindowRect(hwnd) is { } current && current.RoughlyEquals(rect, Tolerance)) return;
        Win32.SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, rect.Width, rect.Height,
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_ASYNCWINDOWPOS);
    }

    /// <summary>
    /// Move several windows (rects already include margins) in one <c>DeferWindowPos</c> batch, so they move
    /// together without tearing. Hung windows are left out (the batch is synchronous). If the batch fails —
    /// e.g. one window is elevated — fall back to moving each one asynchronously.
    /// </summary>
    public static void PlaceTogether(IReadOnlyList<(IntPtr Hwnd, PixelRect Rect)> moves)
    {
        var live = moves.Where(m => Win32.IsWindow(m.Hwnd) && !Win32.IsIconic(m.Hwnd) && !Win32.IsHungAppWindow(m.Hwnd)).ToList();
        if (live.Count == 0) return;

        const uint flags = Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;
        var info = Win32.BeginDeferWindowPos(live.Count);
        foreach (var (hwnd, r) in live)
        {
            if (info == IntPtr.Zero) break;
            info = Win32.DeferWindowPos(info, hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, flags);
        }
        if (info != IntPtr.Zero && Win32.EndDeferWindowPos(info)) return;

        foreach (var (hwnd, r) in live)
            Win32.SetWindowPos(hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, flags | Win32.SWP_ASYNCWINDOWPOS);
    }
}
