using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Flamingle.App.Native;

/// <summary>
/// The documented <c>IVirtualDesktopManager</c>: which desktop a window is on, and moving our own windows
/// between desktops (Windows only lets a process move its own). Best effort — if the shell isn't there or
/// restarts, calls return null / false and the next call tries a fresh instance.
/// </summary>
internal static class VirtualDesktops
{
    private static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private static IVirtualDesktopManager? _manager;

    /// <summary>The desktop the window is on, or null if it can't be read (gone, or no shell).</summary>
    public static Guid? DesktopOf(IntPtr hwnd)
    {
        if (Manager() is not { } m) return null;
        try
        {
            return m.GetWindowDesktopId(hwnd, out var id) == 0 && id != Guid.Empty ? id : null;
        }
        catch (COMException)
        {
            Reset();
            return null;
        }
    }

    /// <summary>True if the window is on the desktop the user is looking at (null if it can't be read).</summary>
    public static bool? IsOnCurrent(IntPtr hwnd)
    {
        if (Manager() is not { } m) return null;
        try
        {
            return m.IsWindowOnCurrentVirtualDesktop(hwnd, out var on) == 0 ? on != 0 : null;
        }
        catch (COMException)
        {
            Reset();
            return null;
        }
    }

    /// <summary>
    /// A short label for a desktop — its custom name, else "Desktop N" — or null with only one desktop (or if
    /// it can't be told). Reads the shell's own registry record of the desktops: undocumented, so best effort.
    /// </summary>
    public static string? Label(Guid desktop)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopsKey);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] ids || ids.Length < 32) return null;

            for (var i = 0; i + 16 <= ids.Length; i += 16)
            {
                if (new Guid(ids.AsSpan(i, 16)) != desktop) continue;
                using var named = key.OpenSubKey($@"Desktops\{desktop:B}");
                return named?.GetValue("Name") is string { Length: > 0 } name ? name : $"Desktop {i / 16 + 1}";
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
        }
        return null;
    }

    private const string DesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    /// <summary>Move one of Flamingle's own windows to <paramref name="desktop"/>.</summary>
    public static bool MoveTo(IntPtr hwnd, Guid desktop)
    {
        if (hwnd == IntPtr.Zero || Manager() is not { } m) return false;
        try
        {
            return m.MoveWindowToDesktop(hwnd, ref desktop) == 0;
        }
        catch (COMException)
        {
            Reset();
            return false;
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "A COM class from its CLSID: built-in COM creates it, no constructor of ours is involved.")]
    private static IVirtualDesktopManager? Manager()
    {
        if (_manager is not null) return _manager;
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_VirtualDesktopManager);
            _manager = type is null ? null : Activator.CreateInstance(type) as IVirtualDesktopManager;
        }
        catch (COMException)
        {
            _manager = null;
        }
        return _manager;
    }

    private static void Reset()
    {
        if (_manager is not null) Marshal.ReleaseComObject(_manager);
        _manager = null;
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }
}
