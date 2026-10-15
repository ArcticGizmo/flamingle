using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Flamingle.Core.Saved;

namespace Flamingle.App.Native;

/// <summary>
/// Starts a planned window's app: an exe through the shell (so an app that asks for elevation gets its UAC prompt,
/// and App Paths names like <c>wt.exe</c> resolve), or a Store app through <c>IApplicationActivationManager</c>.
/// </summary>
internal static class AppLauncher
{
    /// <summary>Start it; null on success, else why not (for a toast).</summary>
    public static string? Start(PlannedWindow p)
    {
        try
        {
            return p.Recipe.Kind switch
            {
                LaunchKind.Program => StartProgram(p),
                LaunchKind.StoreApp => StartStoreApp(p),
                _ => null,
            };
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ERROR_CANCELLED)
        {
            return "its administrator prompt was cancelled";
        }
        catch (Exception e) when (e is Win32Exception or COMException or InvalidOperationException or ArgumentException)
        {
            return e.Message;
        }
    }

    private static string? StartProgram(PlannedWindow p)
    {
        var info = new ProcessStartInfo(p.Target)
        {
            Arguments = p.Arguments,
            UseShellExecute = true,
        };
        var folder = p.StartIn ?? (Path.IsPathRooted(p.Target) ? Path.GetDirectoryName(p.Target) : null);
        if (folder is not null) info.WorkingDirectory = folder;
        using var process = Process.Start(info);
        return null;
    }

    private static string? StartStoreApp(PlannedWindow p)
    {
        var manager = (IApplicationActivationManager)new ApplicationActivationManager();
        try
        {
            var hr = manager.ActivateApplication(p.Target, string.IsNullOrEmpty(p.Arguments) ? null : p.Arguments, 0, out _);
            return hr >= 0 ? null : Marshal.GetExceptionForHR(hr)?.Message ?? $"error 0x{hr:X8}";
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private const int ERROR_CANCELLED = 1223;

    [ComImport]
    [Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);

        // Declared to keep the vtable in order; unused.
        [PreserveSig]
        int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray,
            [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);

        [PreserveSig]
        int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager
    {
    }
}
