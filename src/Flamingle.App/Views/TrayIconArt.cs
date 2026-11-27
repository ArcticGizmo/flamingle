using Avalonia.Controls;
using Avalonia.Platform;

namespace Flamingle.App.Views;

/// <summary>
/// Flamingle's icons, rasterised from <c>flamingle.svg</c> by <c>tools/IconGen</c>. The tray always gets the 32 px
/// frame: Avalonia hands a PNG to the shell at its own size, and Explorer shrinking it to the tray stays sharp at every
/// scale up to 200 %, whereas a frame smaller than the tray gets stretched and blurs. A dev instance is green instead
/// of pink. See docs/tray-icon-blur.md.
/// </summary>
internal static class TrayIconArt
{
    /// <summary>The multi-resolution product icon (same as the .exe's).</summary>
    public static WindowIcon Product() => new(AssetLoader.Open(Asset("flamingle.ico")));

    public static WindowIcon Create(bool dev) =>
        new(AssetLoader.Open(Asset($"tray/flamingle{(dev ? "-dev" : "")}-32.png")));

    private static Uri Asset(string path) => new($"avares://flamingle/Assets/{path}");
}
