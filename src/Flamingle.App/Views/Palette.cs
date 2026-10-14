using Avalonia.Media;

namespace Flamingle.App.Views;

/// <summary>The few colours Flamingle's code-built windows share. Deliberately plain while we prove things out.</summary>
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

    // Settings window.
    public static readonly IBrush Title = new SolidColorBrush(Color.Parse("#F5F8FA"));
    public static readonly IBrush NavBg = new SolidColorBrush(Color.Parse("#0B0F14"));
    public static readonly IBrush ButtonBg = new SolidColorBrush(Color.Parse("#1E262F"));
    public static readonly IBrush Line =new SolidColorBrush(Color.Parse("#2A333D"));
    public static readonly IBrush OnAccent = new SolidColorBrush(Color.Parse("#0B1220"));
    public static readonly Color ToggleOff = Color.Parse("#3A4450");
}
