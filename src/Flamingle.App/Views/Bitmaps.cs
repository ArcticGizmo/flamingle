using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Flamingle.App.Views;

/// <summary>Raw-pixel helpers between Avalonia bitmaps and the Win32 side (premultiplied BGRA, top row first).</summary>
internal static class Bitmaps
{
    /// <summary>Render a code-built control into pixels at 1 DIP = 1 px.</summary>
    public static byte[] Render(Control control, int width, int height)
    {
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(control);
        return Pixels(bitmap, width, height);
    }

    public static byte[] Pixels(Bitmap bitmap, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, width, height), pin.AddrOfPinnedObject(), pixels.Length, width * 4);
        }
        finally
        {
            pin.Free();
        }
        return pixels;
    }

    /// <summary>An Avalonia bitmap from opaque BGRA pixels.</summary>
    public static WriteableBitmap FromPixels(int width, int height, byte[] bgra)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bitmap.Lock();
        for (var y = 0; y < height; y++)
            Marshal.Copy(bgra, y * width * 4, fb.Address + y * fb.RowBytes, width * 4);
        return bitmap;
    }
}
