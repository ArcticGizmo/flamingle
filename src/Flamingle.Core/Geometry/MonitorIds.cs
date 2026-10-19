namespace Flamingle.Core.Geometry;

/// <summary>
/// Telling monitors apart across reconnects. Windows renumbers a monitor's GDI name (<c>\\.\DISPLAY12</c>) every time
/// it reconnects — a USB (DisplayLink) dock does that on every wake — but its PnP instance id stays put, and unlike the
/// EDID model it tells two identical monitors apart.
/// </summary>
public static class MonitorIds
{
    /// <summary>
    /// The PnP instance id in a monitor's device interface path:
    /// <c>\\?\DISPLAY#ACR03E3#8&amp;3157d11d&amp;0&amp;UID256#{e6f07b5f-…}</c> → <c>DISPLAY\ACR03E3\8&amp;3157D11D&amp;0&amp;UID256</c>.
    /// Null if it isn't one.
    /// </summary>
    public static string? FromInterfacePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) p = p[4..];
        var guid = p.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid >= 0) p = p[..guid];
        var parts = p.Split('#');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace)) return null;
        return string.Join('\\', parts).ToUpperInvariant();
    }

    /// <summary>For showing: <c>\\.\DISPLAY1</c> → <c>DISPLAY1</c>; an instance id → its model code (<c>ACR03E3</c>).</summary>
    public static string ShortName(string deviceName)
    {
        var parts = deviceName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts is ["DISPLAY", var model, _] ? model : deviceName.TrimStart('\\', '.');
    }
}
