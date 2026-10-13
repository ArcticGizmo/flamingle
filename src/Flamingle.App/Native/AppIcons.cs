using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Flamingle.App.Native;

/// <summary>
/// Reads another app's window icon and turns it into an Avalonia bitmap — for the flock taskbar buttons,
/// whose icon is made from the member apps' icons. Uses <c>WM_GETICON</c> with a timeout (a hung app can't
/// stall us) and falls back to the window class icon. The HICON is drawn into a 32-bit DIB with
/// <c>DrawIconEx</c>, which keeps the icon's alpha.
/// </summary>
internal static class AppIcons
{
    private const uint WM_GETICON = 0x007F;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int ICON_SMALL2 = 2;
    private const int GCLP_HICON = -14;
    private const int GCLP_HICONSM = -34;
    private const uint SMTO_ABORTIFHUNG = 0x0002;
    private const uint DI_NORMAL = 0x0003;

    public static Bitmap? For(IntPtr hwnd, int size = 32)
    {
        var icon = IconHandle(hwnd);
        return icon == IntPtr.Zero ? null : Render(icon, size);
    }

    private static IntPtr IconHandle(IntPtr hwnd)
    {
        foreach (var kind in new[] { ICON_BIG, ICON_SMALL2, ICON_SMALL })
        {
            if (SendMessageTimeoutW(hwnd, WM_GETICON, (IntPtr)kind, IntPtr.Zero, SMTO_ABORTIFHUNG, 100, out var result) != IntPtr.Zero
                && result != IntPtr.Zero)
                return result;
        }

        var cls = GetClassLongPtrW(hwnd, GCLP_HICON);
        return cls != IntPtr.Zero ? cls : GetClassLongPtrW(hwnd, GCLP_HICONSM);
    }

    private static WriteableBitmap? Render(IntPtr icon, int size)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,   // top-down
            biPlanes = 1,
            biBitCount = 32,
        };

        var dc = CreateCompatibleDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return null;
        var dib = CreateDIBSection(dc, ref header, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero) { DeleteDC(dc); return null; }

        var old = SelectObject(dc, dib);
        try
        {
            if (!DrawIconEx(dc, 0, 0, icon, size, size, 0, IntPtr.Zero, DI_NORMAL)) return null;

            var pixels = new byte[size * size * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // Old-style icons without an alpha channel come out fully transparent: make drawn pixels opaque.
            var hasAlpha = false;
            for (var i = 3; i < pixels.Length; i += 4)
                if (pixels[i] != 0) { hasAlpha = true; break; }
            if (!hasAlpha)
                for (var i = 3; i < pixels.Length; i += 4)
                    if (pixels[i - 1] != 0 || pixels[i - 2] != 0 || pixels[i - 3] != 0) pixels[i] = 0xFF;

            var bitmap = new WriteableBitmap(new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = bitmap.Lock())
            {
                for (var y = 0; y < size; y++)
                    Marshal.Copy(pixels, y * size * 4, fb.Address + y * fb.RowBytes, size * 4);
            }
            return bitmap;
        }
        finally
        {
            SelectObject(dc, old);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClassLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy,
        uint step, IntPtr hbrFlickerFreeDraw, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}
