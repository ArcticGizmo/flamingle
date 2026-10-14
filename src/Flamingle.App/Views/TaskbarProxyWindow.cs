using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Flamingle.App.Native;

namespace Flamingle.App.Views;

/// <summary>
/// A stand-in window whose only job is to put a flock on the taskbar. It lives minimised and off-screen,
/// under its own AppUserModelID (so it gets its own button and icon rather than stacking under Flamingle's).
/// Clicking the button "restores" it — which this turns straight into <see cref="Clicked"/> and drops back
/// to minimised. Closing it from the taskbar raises <see cref="CloseRequested"/> instead of closing.
/// With custom previews on, it also relays the taskbar's preview requests and thumbnail-button clicks.
/// </summary>
internal sealed class TaskbarProxyWindow : Window
{
    private readonly string _appId;
    private bool _armed;
    private bool _allowClose;

    public TaskbarProxyWindow(Guid flockId)
    {
        _appId = $"Flamingle.Flock.{flockId:N}";

        ShowInTaskbar = true;
        ShowActivated = false;
        CanResize = false;
        WindowDecorations = WindowDecorations.None;
        Background = Brushes.Transparent;
        Width = 1;
        Height = 1;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(-20000, -20000);

        Win32Properties.AddWndProcHookCallback(this, WndProc);
    }

    /// <summary>The native window handle (zero until it's been shown).</summary>
    public IntPtr Handle => TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    /// <summary>The taskbar wants a preview (custom previews on): (max width, max height) in pixels.</summary>
    public event Action<int, int>? ThumbnailRequested;

    /// <summary>The taskbar button now exists — thumbnail buttons can be added.</summary>
    public event Action? ButtonCreated;

    /// <summary>A button under the preview was clicked (its id).</summary>
    public event Action<uint>? ThumbButtonClicked;

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == TaskbarThumbnails.WM_DWMSENDICONICTHUMBNAIL)
        {
            var l = (long)lParam;
            ThumbnailRequested?.Invoke((int)((l >> 16) & 0xFFFF), (int)(l & 0xFFFF));
            handled = true;
        }
        else if (msg == TaskbarThumbnails.WM_TASKBARBUTTONCREATED)
        {
            ButtonCreated?.Invoke();
        }
        else if (msg == TaskbarThumbnails.WM_COMMAND && (((long)wParam >> 16) & 0xFFFF) == TaskbarThumbnails.THBN_CLICKED)
        {
            ThumbButtonClicked?.Invoke((uint)((long)wParam & 0xFFFF));
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>The taskbar button was clicked (or the proxy was picked in Alt+Tab).</summary>
    public event Action? Clicked;

    /// <summary>"Close window" from the taskbar.</summary>
    public event Action? CloseRequested;

    /// <summary>Really close (when the flock goes away).</summary>
    public void CloseForGood()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        AppUserModel.SetId(Handle, _appId);
        WindowState = WindowState.Minimized;
        // Only treat state changes as clicks once we've settled into minimised.
        Dispatcher.UIThread.Post(() => _armed = true, DispatcherPriority.Background);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != WindowStateProperty || !_armed) return;
        if (change.GetNewValue<WindowState>() == WindowState.Minimized) return;

        // Act while we still hold the foreground (the user just clicked us), then tuck back away.
        Clicked?.Invoke();
        Dispatcher.UIThread.Post(() => WindowState = WindowState.Minimized);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose) return;
        e.Cancel = true;
        CloseRequested?.Invoke();
    }
}
