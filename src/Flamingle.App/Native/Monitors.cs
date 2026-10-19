using System.Runtime.InteropServices;
using Flamingle.Core.Geometry;

namespace Flamingle.App.Native;

/// <summary>Every display's physical geometry, work area and DPI, straight from Win32 (mirrors shrike).</summary>
internal static class Monitors
{
    // GDI name → stable id. A GDI name can come to mean another monitor (a different one plugged into the same port),
    // so this is forgotten on every display change.
    private static readonly Dictionary<string, string> Ids = [];

    /// <summary>All monitors, primary first, then left-to-right.</summary>
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Win32.MonitorEnumProc callback = (hMonitor, _, _, _) =>
        {
            var info = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>(), szDevice = "" };
            if (Win32.GetMonitorInfoW(hMonitor, ref info))
            {
                var scale = Win32.GetDpiForMonitor(hMonitor, Win32.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 && dpi > 0
                    ? dpi / 96.0
                    : 1.0;
                list.Add(new MonitorInfo(
                    hMonitor,
                    StableId(info.szDevice),
                    ToRect(info.rcMonitor),
                    ToRect(info.rcWork),
                    scale,
                    (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0,
                    info.szDevice));
            }
            return true;
        };
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    /// <summary>The displays changed: look each monitor's id up afresh.</summary>
    public static void Forget()
    {
        lock (Ids) Ids.Clear();
    }

    /// <summary>"DISPLAY3" for a monitor's key, if it's connected (else a short form of the key itself).</summary>
    public static string ShortNameOf(string deviceName)
        => All().FirstOrDefault(m => m.DeviceName == deviceName)?.ShortName ?? MonitorIds.ShortName(deviceName);

    /// <summary>The PnP instance id of the (first active) monitor on a GDI display — see <see cref="MonitorIds"/>.
    /// Falls back to the GDI name.</summary>
    private static string StableId(string gdiName)
    {
        lock (Ids)
        {
            if (Ids.TryGetValue(gdiName, out var known)) return known;
        }

        var id = gdiName;
        var dd = new Win32.DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<Win32.DISPLAY_DEVICEW>() };
        for (uint i = 0; Win32.EnumDisplayDevicesW(gdiName, i, ref dd, Win32.EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            if ((dd.StateFlags & Win32.DISPLAY_DEVICE_ACTIVE) != 0 && MonitorIds.FromInterfacePath(dd.DeviceID) is { } found)
            {
                id = found;
                break;
            }
            dd = new Win32.DISPLAY_DEVICEW { cb = (uint)Marshal.SizeOf<Win32.DISPLAY_DEVICEW>() };
        }

        lock (Ids) Ids[gdiName] = id;
        return id;
    }

    private static PixelRect ToRect(Win32.RECT r) => PixelRect.FromEdges(r.left, r.top, r.right, r.bottom);
}
