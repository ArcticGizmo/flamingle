using Avalonia.Controls;
using Avalonia.Platform;
using Flamingle.App.Native;

namespace Flamingle.App.Views;

/// <summary>
/// Flamingle's icons, rasterised from <c>flamingle.svg</c> by <c>tools/IconGen</c>. The tray gets a frame rendered at
/// exactly the notification area's pixel size: Avalonia resamples whatever it's given to SM_CXSMICON, and that
/// resampling is what blurs a tray icon. A dev instance is green instead of pink.
/// </summary>
internal static class TrayIconArt
{
    // Must match traySizes in tools/IconGen/Program.cs.
    private static readonly int[] TraySizes = [16, 20, 24, 28, 32, 36, 40, 48];

    /// <summary>The multi-resolution product icon (same as the .exe's).</summary>
    public static WindowIcon Product() => new(AssetLoader.Open(Asset("flamingle.ico")));

    public static WindowIcon Create(bool dev)
    {
        var want = TrayPixelSize();
        // An exact frame exists for every standard scale; otherwise the next size up (downscaling stays crisper).
        var size = TraySizes.FirstOrDefault(s => s >= want, TraySizes[^1]);
        return new WindowIcon(AssetLoader.Open(Asset($"tray/flamingle{(dev ? "-dev" : "")}-{size}.png")));
    }

    /// <summary>SM_CXSMICON at the primary monitor's DPI — the size the taskbar's notification area draws at.</summary>
    private static int TrayPixelSize()
    {
        var monitor = Win32.MonitorFromPoint(default, Win32.MONITOR_DEFAULTTOPRIMARY);
        var dpi = Win32.GetDpiForMonitor(monitor, Win32.MDT_EFFECTIVE_DPI, out var x, out _) == 0 && x > 0 ? x : 96u;
        var size = Win32.GetSystemMetricsForDpi(Win32.SM_CXSMICON, dpi);
        return size > 0 ? size : 16;
    }

    private static Uri Asset(string path) => new($"avares://flamingle/Assets/{path}");
}
