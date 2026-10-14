using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;
using Flamingle.App.Native;

namespace Flamingle.App.Views;

/// <summary>The icons on the buttons under a flock's taskbar preview, drawn in code (white on the dark
/// thumbnail toolbar) at the system's small-icon size. Made once; never freed (they live as long as the app).</summary>
internal static class ThumbGlyphs
{
    /// <summary>Minimise / restore: a window over a minimise bar.</summary>
    public static IntPtr Toggle() => Icon("M2.5,2.5 H13.5 V10.5 H2.5 Z M2.5,5 H13.5 M4,13.5 H12", fill: false);

    /// <summary>Dissolve: three windows pulled apart.</summary>
    public static IntPtr Dissolve() => Icon("M1,5 H5 V10 H1 Z M6,5 H10 V10 H6 Z M11,5 H15 V10 H11 Z", fill: true);

    private static IntPtr Icon(string data, bool fill)
    {
        var size = SmallIconSize();
        var path = new Path
        {
            Data = Geometry.Parse(data),
            Stroke = fill ? null : Brushes.White,
            StrokeThickness = 1.4,
            Fill = fill ? Brushes.White : null,
        };
        var view = new Viewbox
        {
            Width = size,
            Height = size,
            Child = new Canvas { Width = 16, Height = 16, Children = { path } },
        };
        return Gdi.CreateIcon(size, size, Bitmaps.Render(view, size, size));
    }

    private static int SmallIconSize()
    {
        var scale = Monitors.All().FirstOrDefault(m => m.IsPrimary)?.Scale ?? 1.0;
        return Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, (uint)Math.Round(96 * scale));
    }
}
