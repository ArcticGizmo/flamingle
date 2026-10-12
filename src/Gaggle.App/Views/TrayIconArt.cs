using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Gaggle.App.Views;

/// <summary>
/// Draws the tray icon at runtime — three overlapping window tiles, a little gaggle — so there's no asset
/// pipeline to maintain while the look is still up in the air. A dev instance is pink.
/// </summary>
internal static class TrayIconArt
{
    private const int Size = 32;

    public static WindowIcon Create(bool dev)
    {
        var accent = dev ? Palette.DevAccent : Palette.Accent;
        var canvas = new Canvas { Width = Size, Height = Size };

        AddTile(canvas, 1, 3, accent, 0.45);
        AddTile(canvas, 7, 9, accent, 0.7);
        AddTile(canvas, 13, 15, accent, 1.0);

        canvas.Measure(new Size(Size, Size));
        canvas.Arrange(new Rect(0, 0, Size, Size));

        var bitmap = new RenderTargetBitmap(new PixelSize(Size, Size), new Vector(96, 96));
        bitmap.Render(canvas);
        return new WindowIcon(bitmap);
    }

    private static void AddTile(Canvas canvas, double x, double y, Color color, double opacity)
    {
        var tile = new Border
        {
            Width = 18,
            Height = 15,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(color, opacity),
            // A dark rim keeps the tiles legible on a light taskbar.
            BorderBrush = new SolidColorBrush(Color.Parse("#0B1220")),
            BorderThickness = new Thickness(1),
        };
        Canvas.SetLeft(tile, x);
        Canvas.SetTop(tile, y);
        canvas.Children.Add(tile);
    }
}
