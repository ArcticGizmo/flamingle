using Flamingle.Core.Saved;

namespace Flamingle.App.Native;

/// <summary>Which app a window belongs to — the exe and app id that capture records and relaunch matches on.</summary>
internal static class WindowIdentity
{
    private const string UwpFrameClass = "ApplicationFrameWindow";
    private const string UwpCoreClass = "Windows.UI.Core.CoreWindow";

    /// <summary>A window as relaunch matches it.</summary>
    public static OpenWindow Of(IntPtr hwnd)
    {
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        pid = AppProcess(hwnd, pid);
        return new OpenWindow(
            hwnd,
            ProcessInfo.ImagePath(pid),
            AppUserModel.GetId(hwnd) ?? ProcessInfo.PackagedAppId(pid),
            WindowQuery.ClassName(hwnd),
            WindowQuery.Title(hwnd));
    }

    /// <summary>The process that really owns the window. A Store app's window is a frame owned by
    /// <c>ApplicationFrameHost</c>; the app itself owns the core window inside it. (A minimised Store app's core
    /// window can be detached from the frame, in which case we're left with the host.)</summary>
    public static uint AppProcess(IntPtr hwnd, uint pid)
    {
        if (WindowQuery.ClassName(hwnd) != UwpFrameClass) return pid;
        var core = Win32.FindWindowExW(hwnd, IntPtr.Zero, UwpCoreClass, null);
        if (core == IntPtr.Zero) return pid;
        Win32.GetWindowThreadProcessId(core, out var appPid);
        return appPid;
    }
}
