using System.Runtime.InteropServices;

namespace Flamingle.App.Native;

/// <summary>
/// GDI bitmap plumbing: 32-bit top-down DIB sections (for DWM previews and window captures) and icons made
/// from raw pixels (for the thumbnail toolbar). Pixels are BGRA, top row first.
/// </summary>
internal static class Gdi
{
    /// <summary>A 32-bit DIB section holding <paramref name="bgra"/>. The caller owns it (<see cref="DeleteObject"/>).</summary>
    public static IntPtr CreateDib(int width, int height, byte[] bgra)
    {
        var bitmap = CreateDib(width, height, out var bits);
        if (bitmap != IntPtr.Zero) Marshal.Copy(bgra, 0, bits, Math.Min(bgra.Length, width * height * 4));
        return bitmap;
    }

    public static IntPtr CreateDib(int width, int height, out IntPtr bits)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,   // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        return CreateDIBSection(IntPtr.Zero, ref header, 0, out bits, IntPtr.Zero, 0);
    }

    /// <summary>An icon from premultiplied BGRA pixels. The caller owns it (<see cref="DestroyIcon"/>).</summary>
    public static IntPtr CreateIcon(int width, int height, byte[] bgra)
    {
        var color = CreateDib(width, height, bgra);
        var mask = CreateBitmap(width, height, 1, 1, IntPtr.Zero);
        try
        {
            var info = new ICONINFO { fIcon = true, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref info);
        }
        finally
        {
            DeleteObject(color);
            DeleteObject(mask);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi, uint usage,
        out IntPtr bits, IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, IntPtr bits);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr h);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr hdc);
}
