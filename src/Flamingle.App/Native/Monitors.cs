using System.Runtime.InteropServices;
using Flamingle.Core.Geometry;

namespace Flamingle.App.Native;

/// <summary>Every display's physical geometry, work area and DPI, straight from Win32 (mirrors shrike).</summary>
internal static class Monitors
{
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
                    info.szDevice,
                    ToRect(info.rcMonitor),
                    ToRect(info.rcWork),
                    scale,
                    (info.dwFlags & Win32.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        };
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ThenBy(m => m.Bounds.Y).ToList();
    }

    private static PixelRect ToRect(Win32.RECT r) => PixelRect.FromEdges(r.left, r.top, r.right, r.bottom);
}
