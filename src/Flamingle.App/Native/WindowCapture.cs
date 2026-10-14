using System.Runtime.InteropServices;
using Flamingle.Core.Geometry;

namespace Flamingle.App.Native;

/// <summary>A picture of a window's visible frame: opaque BGRA pixels, top row first, and where it was on screen.</summary>
internal sealed record CapturedWindow(PixelRect Bounds, int Width, int Height, byte[] Pixels);

/// <summary>
/// Pictures of other apps' windows, for the flock taskbar preview. Uses <c>PrintWindow</c> with
/// <c>PW_RENDERFULLCONTENT</c>, which asks DWM for the window's content (so it works for DirectX / Chromium
/// windows and for windows that are covered). It's a cross-process call, so: skip hung windows, and call it
/// off the UI thread. Minimised windows can't be captured (there's nothing drawn) — callers cache a picture
/// from before the minimise instead.
/// </summary>
internal static class WindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 0x2;

    // Captures are only ever shown as thumbnails — no point holding more pixels than this per window.
    private const int MaxDimension = 1280;

    /// <summary>Capture the window's visible frame, or null (minimised, hung, gone, or it drew nothing).
    /// Safe to call from any thread.</summary>
    public static CapturedWindow? Capture(IntPtr hwnd)
    {
        if (!Win32.IsWindow(hwnd) || Win32.IsIconic(hwnd) || Win32.IsHungAppWindow(hwnd)) return null;
        if (!Win32.GetWindowRect(hwnd, out var r)) return null;
        var window = PixelRect.FromEdges(r.left, r.top, r.right, r.bottom);
        if (window.Width <= 0 || window.Height <= 0) return null;

        // PrintWindow draws the whole window, invisible resize borders included; crop to the visible frame.
        var frame = WindowQuery.VisibleBounds(hwnd);
        if (frame.Width <= 0 || frame.Height <= 0) frame = window;
        var cropX = Math.Clamp(frame.X - window.X, 0, window.Width - 1);
        var cropY = Math.Clamp(frame.Y - window.Y, 0, window.Height - 1);
        var cropW = Math.Min(frame.Width, window.Width - cropX);
        var cropH = Math.Min(frame.Height, window.Height - cropY);

        var dc = Gdi.CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        var dib = Gdi.CreateDib(window.Width, window.Height, out var bits);
        if (dib == IntPtr.Zero) { Gdi.DeleteDC(dc); return null; }
        var old = Gdi.SelectObject(dc, dib);
        try
        {
            if (!PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)) return null;

            // Copy the cropped frame out, downsampling (nearest pixel) if it's bigger than we'll ever show.
            var step = Math.Max(1, (int)Math.Ceiling(Math.Max(cropW, cropH) / (double)MaxDimension));
            var w = cropW / step;
            var h = cropH / step;
            var src = new byte[window.Width * 4];
            var pixels = new byte[w * h * 4];
            var anyDrawn = false;
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(bits + ((cropY + y * step) * window.Width * 4), src, 0, src.Length);
                for (var x = 0; x < w; x++)
                {
                    var s = (cropX + x * step) * 4;
                    var d = (y * w + x) * 4;
                    pixels[d] = src[s];
                    pixels[d + 1] = src[s + 1];
                    pixels[d + 2] = src[s + 2];
                    pixels[d + 3] = 0xFF;   // PrintWindow leaves the alpha channel undefined
                    anyDrawn |= (src[s] | src[s + 1] | src[s + 2]) != 0;
                }
            }

            // All black: the app refused (protected content, some games) — better to show its icon instead.
            return anyDrawn ? new CapturedWindow(frame, w, h, pixels) : null;
        }
        finally
        {
            Gdi.SelectObject(dc, old);
            Gdi.DeleteObject(dib);
            Gdi.DeleteDC(dc);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
