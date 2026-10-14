using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Flamingle.App.Native;

namespace Flamingle.App.Views;

/// <summary>
/// A small self-dismissing notice at the bottom centre of the monitor under the cursor. Borderless, topmost,
/// never activated (so it can't steal focus from a flock we've just restored). Click or wait to dismiss.
/// </summary>
internal sealed class ToastWindow : Window
{
    // Gap between the toast and the bottom of the work area (DIP).
    private const double BottomMargin = 24;

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

        // The monitor the user is looking at — the one under the cursor — not the primary.
        var screen = (Win32.GetCursorPos(out var cursor) ? Screens.ScreenFromPoint(new PixelPoint(cursor.x, cursor.y)) : null)
            ?? Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is not null && Content is Control content)
        {
            // Sized at the target monitor's DPI (it may differ from wherever the window was born).
            content.Measure(Size.Infinity);
            var b = screen.WorkingArea;
            var wpx = (int)Math.Ceiling(content.DesiredSize.Width * screen.Scaling);
            var hpx = (int)Math.Ceiling(content.DesiredSize.Height * screen.Scaling);
            var margin = (int)(BottomMargin * screen.Scaling);
            Position = new PixelPoint(b.X + (b.Width - wpx) / 2, b.Bottom - margin - hpx);
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
