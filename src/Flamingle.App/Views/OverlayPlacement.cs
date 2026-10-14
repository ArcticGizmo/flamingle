using Avalonia;
using Avalonia.Controls;
using Flamingle.App.Native;
using PixelRect = Flamingle.Core.Geometry.PixelRect;

namespace Flamingle.App.Views;

/// <summary>
/// Moves Flamingle's own overlay windows (the edit frame, divider strips) without touching their stacking order.
/// Setting an Avalonia window's <c>Position</c> goes through <c>SetWindowPos</c> without <c>SWP_NOZORDER</c>, which
/// also lifts it to the top of the topmost band — so the edit frame, moved on every frame of a drag, ended up over
/// the split strips and the panel and swallowed their clicks.
/// </summary>
internal static class OverlayPlacement
{
    /// <summary>Put <paramref name="window"/> over <paramref name="rect"/> (physical px) on a monitor at
    /// <paramref name="scale"/>, leaving its Z-order alone.</summary>
    public static void Place(Window window, PixelRect rect, double scale)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero)
        {
            // Not shown yet: Avalonia applies these when it creates the window.
            window.Position = new PixelPoint(rect.X, rect.Y);
            window.Width = Math.Max(1, rect.Width / scale);
            window.Height = Math.Max(1, rect.Height / scale);
            return;
        }
        Win32.SetWindowPos(hwnd, IntPtr.Zero, rect.X, rect.Y, Math.Max(1, rect.Width), Math.Max(1, rect.Height),
            Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
    }
}
