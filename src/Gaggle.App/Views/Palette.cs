using Avalonia.Media;

namespace Gaggle.App.Views;

/// <summary>The few colours Gaggle's code-built windows share. Deliberately plain while we prove things out.</summary>
internal static class Palette
{
    public static readonly Color Accent = Color.Parse("#7DD3FC");      // sky
    public static readonly Color DevAccent = Color.Parse("#FF4FA3");   // pink — marks a dev instance
    public static readonly IBrush AccentBrush = new SolidColorBrush(Accent);
    public static readonly IBrush Fg = new SolidColorBrush(Color.Parse("#E6EDF3"));
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B98A5"));
    public static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#11161C"));
    public static readonly IBrush Row = new SolidColorBrush(Color.Parse("#1A2129"));
    public static readonly IBrush ToastBg = new SolidColorBrush(Color.Parse("#F20D1218"));
}
