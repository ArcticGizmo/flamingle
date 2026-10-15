using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.App.Native;
using Flamingle.App.Views.Settings;
using Flamingle.Core.Geometry;

namespace Flamingle.App.Views;

/// <summary>
/// "Where should it load?" — one of these covers each monitor while a saved flock waits to load: the screen dimmed,
/// with a card saying what's in its zones now (and that loading here minimises it) and whether the flock was saved
/// here. Click anywhere on a monitor to load onto it; Cancel or Esc to stop. Topmost; activatable, so Esc works.
/// </summary>
internal sealed class LoadTargetWindow : Window
{
    private static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#0B0F14"), 0.45);
    private static readonly IBrush DimHover = new SolidColorBrush(Color.Parse("#0B0F14"), 0.25);

    private readonly Border _card;

    /// <param name="flockName">The saved flock being loaded.</param>
    /// <param name="savedHere">The flock was saved on (or would go back to) this monitor.</param>
    /// <param name="occupant">What's in this monitor's zones now (null: nothing).</param>
    public LoadTargetWindow(MonitorInfo monitor, string flockName, bool savedHere, string? occupant)
    {
        WindowDecorations = WindowDecorations.None;
        Background = Dim;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = new Cursor(StandardCursorType.Hand);

        var body = new StackPanel { Spacing = 8, Width = 360 };
        var title = new WrapPanel();
        title.Children.Add(new TextBlock
        {
            Text = $"Load \"{flockName}\" here", FontSize = 18, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0),
        });
        if (savedHere) title.Children.Add(SettingsUi.Pill("saved here", Palette.ButtonBg, Palette.AccentBrush));
        body.Children.Add(title);
        body.Children.Add(new TextBlock { Text = monitor.Label, FontSize = 12, Foreground = Palette.Muted });
        body.Children.Add(new TextBlock
        {
            Text = occupant is null
                ? "Nothing in its zones now."
                : $"{occupant} — minimised when this loads (restore it later with its slot hotkey or taskbar button).",
            FontSize = 13, Foreground = occupant is null ? Palette.Fg : SettingsUi.WarnBrush, TextWrapping = TextWrapping.Wrap,
        });

        var load = SettingsUi.FlatButton("Load here", () => Choose());
        load.Background = Palette.AccentBrush;
        load.Foreground = Palette.OnAccent;
        var cancel = SettingsUi.FlatButton("Cancel", () => Cancelled?.Invoke());
        body.Children.Add(SettingsUi.ButtonRow(load, cancel));

        _card = new Border
        {
            Background = Palette.Panel, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(20, 16),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Cursor = new Cursor(StandardCursorType.Arrow), Child = body,
        };
        Content = _card;

        PointerEntered += (_, _) =>
        {
            Background = DimHover;
            _card.BorderBrush = Palette.AccentBrush;
        };
        PointerExited += (_, _) =>
        {
            Background = Dim;
            _card.BorderBrush = Palette.Line;
        };
        OverlayPlacement.Place(this, monitor.WorkArea, monitor.Scale);
    }

    public event Action? Chosen;

    public event Action? Cancelled;

    private void Choose() => Chosen?.Invoke();

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        // Anywhere on the screen but the card's buttons (which handle their own clicks).
        if (!e.Handled && e.InitialPressMouseButton == MouseButton.Left) Choose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancelled?.Invoke();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return;
        var ex = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex | Win32.WS_EX_TOOLWINDOW));
    }

    /// <summary>
    /// Ask which monitor to load <paramref name="flockName"/> onto: one of these on every monitor, the first time
    /// any is clicked (or Cancel / Esc) closes them all.
    /// </summary>
    /// <param name="savedOn">The monitor it would load onto as saved.</param>
    /// <param name="occupantOf">What's in a monitor's zones now, by device name (null: nothing).</param>
    public static void Ask(string flockName, IReadOnlyList<MonitorInfo> monitors, string savedOn, Func<string, string?> occupantOf,
        Action<MonitorInfo> chosen)
    {
        var windows = new List<LoadTargetWindow>();
        var done = false;
        void Finish(MonitorInfo? pick)
        {
            if (done) return;
            done = true;
            foreach (var w in windows) w.Close();
            if (pick is not null) chosen(pick);
        }

        foreach (var m in monitors)
        {
            var monitor = m;
            var w = new LoadTargetWindow(m, flockName, m.DeviceName == savedOn, occupantOf(m.DeviceName));
            w.Chosen += () => Finish(monitor);
            w.Cancelled += () => Finish(null);
            windows.Add(w);
            w.Show();
        }
        // The keyboard goes to the one the flock would load onto as saved.
        var home = monitors.ToList().FindIndex(m => m.DeviceName == savedOn);
        if (home >= 0) windows[home].Activate();
    }
}
