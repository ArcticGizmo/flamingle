namespace Flamingle.Core.Taskbar;

/// <summary>
/// The number on a flock's taskbar icon: which monitor the flock is on. A zone flock belongs to its monitor;
/// any other flock is on the monitor most of its windows are on (ties go to the earliest member).
/// </summary>
public static class FlockBadge
{
    /// <summary>The flock's monitor (device name), or null if none of its windows' monitors are known.</summary>
    public static string? MonitorOf(string? zoneMonitor, IReadOnlyList<string?> memberMonitors)
    {
        if (zoneMonitor is not null) return zoneMonitor;
        return memberMonitors
            .Select((m, i) => (Monitor: m, Index: i))
            .Where(x => x.Monitor is not null)
            .GroupBy(x => x.Monitor!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Min(x => x.Index))
            .Select(g => g.Key)
            .FirstOrDefault();
    }

    /// <summary>The badge text for a monitor: its display number (<c>\\.\DISPLAY2</c> → <c>"2"</c>), the same
    /// number Flamingle uses in zone flock names. Null if it has none.</summary>
    public static string? Number(string? deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return null;
        var end = deviceName.Length;
        var start = end;
        while (start > 0 && char.IsAsciiDigit(deviceName[start - 1])) start--;
        return start < end && int.TryParse(deviceName.AsSpan(start), out var n) ? n.ToString() : null;
    }
}
