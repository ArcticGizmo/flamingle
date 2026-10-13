using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Flamingle.Core.Zones;

namespace Flamingle.App.Views;

/// <summary>A tiny picture of a zone layout — one rounded tile per zone, drawn from its fractions.</summary>
internal sealed class LayoutThumbnail : Canvas
{
    private const double Gap = 2;

    public LayoutThumbnail(ZoneLayout layout, double width, double height, bool selected)
    {
        Width = width;
        Height = height;

        var fill = new SolidColorBrush(Palette.Accent, selected ? 0.75 : 0.28);
        var stroke = new SolidColorBrush(Palette.Accent, selected ? 1.0 : 0.6);

        foreach (var z in layout.Zones)
        {
            var tile = new Border
            {
                Width = Math.Max(1, z.Width * width - Gap),
                Height = Math.Max(1, z.Height * height - Gap),
                CornerRadius = new CornerRadius(2),
                Background = fill,
                BorderBrush = stroke,
                BorderThickness = new Thickness(1),
            };
            SetLeft(tile, z.X * width + Gap / 2);
            SetTop(tile, z.Y * height + Gap / 2);
            Children.Add(tile);
        }
    }
}
