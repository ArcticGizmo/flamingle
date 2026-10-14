using System.Text;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Windows;

namespace Flamingle.App.Native;

/// <summary>
/// Reads the desktop's top-level windows. "Candidates" are roughly what Alt+Tab shows: visible, un-cloaked,
/// titled, app windows (not tool windows / owned popups), excluding Flamingle's own windows and the shell.
/// </summary>
internal static class WindowQuery
{
    private static readonly HashSet<string> ShellClasses =
    [
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "Windows.UI.Core.CoreWindow",
    ];

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>Candidate windows in Z order, topmost first.</summary>
    public static List<WindowSnapshot> Candidates()
    {
        var processNames = new Dictionary<uint, string>();
        var list = new List<WindowSnapshot>();
        foreach (var hwnd in AllTopLevel())
        {
            if (IsCandidate(hwnd))
                list.Add(Snapshot(hwnd, processNames));
        }
        return list;
    }

    /// <summary>Every top-level window handle in Z order, topmost first.</summary>
    public static List<long> ZOrderTopFirst() => AllTopLevel().Select(h => (long)h).ToList();

    /// <summary>True for an Alt+Tab-style app window that isn't ours or the shell's.</summary>
    public static bool IsCandidate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindowVisible(hwnd)) return false;

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnProcessId) return false;

        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return false;

        var exStyle = (long)Win32.GetWindowLongPtrW(hwnd, Win32.GWL_EXSTYLE);
        var appWindow = (exStyle & Win32.WS_EX_APPWINDOW) != 0;
        if (!appWindow)
        {
            if ((exStyle & (Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE)) != 0) return false;
            if (Win32.GetWindow(hwnd, Win32.GW_OWNER) != IntPtr.Zero) return false;
        }

        if (Win32.GetWindowTextLengthW(hwnd) == 0) return false;
        return !ShellClasses.Contains(ClassName(hwnd));
    }

    /// <summary>The bare desktop (what has focus after clicking the wallpaper).</summary>
    public static bool IsDesktop(IntPtr hwnd) => ClassName(hwnd) is "Progman" or "WorkerW";

    /// <summary>Still the same window? (The handle exists and still belongs to the same process.)</summary>
    public static bool IsAlive(FlockMember member)
    {
        var hwnd = (IntPtr)member.Handle;
        if (!Win32.IsWindow(hwnd)) return false;
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == member.ProcessId;
    }

    public static WindowSnapshot Snapshot(IntPtr hwnd) => Snapshot(hwnd, new Dictionary<uint, string>());

    public static FlockMember ToMember(WindowSnapshot w) => new(w.Handle, w.ProcessId, w.ProcessName, w.Title);

    /// <summary>The window rectangle as <c>SetWindowPos</c> understands it (includes invisible borders).</summary>
    public static PixelRect? WindowRect(IntPtr hwnd)
        => Win32.GetWindowRect(hwnd, out var r) ? PixelRect.FromEdges(r.left, r.top, r.right, r.bottom) : null;

    /// <summary>Where a minimised window will come back to (its restored rect, in screen pixels).</summary>
    public static PixelRect? RestoredRect(IntPtr hwnd)
    {
        var placement = new Win32.WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
        if (!Win32.GetWindowPlacement(hwnd, ref placement)) return null;
        var r = placement.rcNormalPosition;
        var rect = PixelRect.FromEdges(r.left, r.top, r.right, r.bottom);

        // The restored rect is in workspace coordinates — offset from the monitor's by any taskbar on its top / left.
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var info = new Win32.MONITORINFOEX { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFOEX>() };
        if (Win32.GetMonitorInfoW(monitor, ref info))
            rect = rect with { X = rect.X + info.rcWork.left - info.rcMonitor.left, Y = rect.Y + info.rcWork.top - info.rcMonitor.top };
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    public enum ShellSpot { Elsewhere, Taskbar, ShellPopup }

    /// <summary>
    /// What's under a screen point, as far as taskbar hover goes: a taskbar (any monitor's), one of the shell's
    /// topmost popups (the button preview, jump lists), or anything else.
    /// </summary>
    public static ShellSpot ShellSpotAt(int x, int y)
    {
        var root = Win32.GetAncestor(Win32.WindowFromPoint(new Win32.POINT { x = x, y = y }), Win32.GA_ROOT);
        if (root == IntPtr.Zero) return ShellSpot.Elsewhere;
        if (ClassName(root) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return ShellSpot.Taskbar;

        var tray = Win32.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return ShellSpot.Elsewhere;
        Win32.GetWindowThreadProcessId(tray, out var shell);
        Win32.GetWindowThreadProcessId(root, out var pid);
        var topmost = ((long)Win32.GetWindowLongPtrW(root, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOPMOST) != 0;
        return pid == shell && topmost && !IsDesktop(root) ? ShellSpot.ShellPopup : ShellSpot.Elsewhere;
    }

    public static string Title(IntPtr hwnd)
    {
        var len = Win32.GetWindowTextLengthW(hwnd);
        if (len <= 0) return string.Empty;
        var sb = new StringBuilder(len + 1);
        Win32.GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static WindowSnapshot Snapshot(IntPtr hwnd, Dictionary<uint, string> processNames)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (!processNames.TryGetValue(pid, out var processName))
            processNames[pid] = processName = ProcessName(pid);

        return new WindowSnapshot(
            hwnd,
            pid,
            processName,
            Title(hwnd),
            VisibleBounds(hwnd),
            Win32.IsIconic(hwnd),
            Win32.IsZoomed(hwnd),
            Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST));
    }

    /// <summary>The visible frame (no invisible resize borders) — what the user sees touching.</summary>
    public static PixelRect VisibleBounds(IntPtr hwnd)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out Win32.RECT r,
                System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) != 0
            && !Win32.GetWindowRect(hwnd, out r))
        {
            return default;
        }
        return PixelRect.FromEdges(r.left, r.top, r.right, r.bottom);
    }

    private static string ProcessName(uint pid)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return string.Empty;
        try
        {
            var sb = new StringBuilder(1024);
            var size = (uint)sb.Capacity;
            return Win32.QueryFullProcessImageNameW(h, 0, sb, ref size)
                ? Path.GetFileNameWithoutExtension(sb.ToString())
                : string.Empty;
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        var len = Win32.GetClassNameW(hwnd, sb, sb.Capacity);
        return len > 0 ? sb.ToString() : string.Empty;
    }

    private static List<IntPtr> AllTopLevel()
    {
        var list = new List<IntPtr>();
        Win32.EnumWindowsProc callback = (hwnd, _) => { list.Add(hwnd); return true; };
        Win32.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return list;
    }
}
