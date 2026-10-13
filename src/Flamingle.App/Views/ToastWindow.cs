using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Flamingle.App.Views;

/// <summary>
/// A small self-dismissing notice near the bottom of the primary screen. Borderless, topmost, never
/// activated (so it can't steal focus from a flock we've just restored). Click or wait to dismiss.
/// </summary>
internal sealed class ToastWindow : Window
{
    private static ToastWindow? _current;
    private readonly DispatcherTimer _life;

    private ToastWindow(string message)
    {
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Content = new Border
        {
            Background = Palette.ToastBg,
            BorderBrush = Palette.AccentBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 11),
            Child = new TextBlock
            {
                Text = message,
                Foreground = Palette.Fg,
                FontSize = 13,
                MaxWidth = 420,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
            },
        };

        _life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _life.Tick += (_, _) => Dismiss();
    }

    /// <summary>Show a toast, replacing any still on screen. Call on the UI thread.</summary>
    public static void Show(string message)
    {
        _current?.Dismiss();
        _current = new ToastWindow(message);
        _current.Show();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null)
        {
            var b = screen.WorkingArea;
            var wpx = (int)(Bounds.Width * screen.Scaling);
            var hpx = (int)(Bounds.Height * screen.Scaling);
            Position = new PixelPoint(b.X + (b.Width - wpx) / 2, b.Y + (int)(b.Height * 0.86) - hpx / 2);
        }

        _life.Start();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Dismiss();
    }

    private void Dismiss()
    {
        _life.Stop();
        if (ReferenceEquals(_current, this)) _current = null;
        Close();
    }
}
