using System.Runtime.InteropServices;

namespace Flamingle.App.Native;

/// <summary>
/// Reads and sets windows' AppUserModelIDs. The taskbar groups buttons by this id, so without it every
/// flock button would stack under Flamingle's own; with a per-flock id each gets its own button (and its own
/// icon) like a separate app. Saved flocks read it to tell apps (and app profiles, PWAs) apart.
/// </summary>
internal static class AppUserModel
{
    /// <summary>The AppUserModelID a window set for itself, or null if it didn't set one (most Win32 apps don't —
    /// the taskbar then falls back to the exe).</summary>
    public static string? GetId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var iid = IID_IPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;

        var value = new PROPVARIANT();
        try
        {
            if (store.GetValue(ref AppUserModelIdKey, out value) != 0 || value.vt != VT_LPWSTR || value.p == IntPtr.Zero)
                return null;
            var id = Marshal.PtrToStringUni(value.p);
            return string.IsNullOrEmpty(id) ? null : id;
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            PropVariantClear(ref value);
            Marshal.ReleaseComObject(store);
        }
    }

    private static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
    private static PROPERTYKEY AppUserModelIdKey = new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5,
    };

    private const ushort VT_LPWSTR = 31;

    /// <summary>Set (or with null, clear) the window's AppUserModelID. Best effort: returns false on failure.</summary>
    public static bool SetId(IntPtr hwnd, string? id)
    {
        if (hwnd == IntPtr.Zero) return false;
        var iid = IID_IPropertyStore;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return false;

        var value = new PROPVARIANT();
        try
        {
            if (id is not null)
            {
                value.vt = VT_LPWSTR;
                value.p = Marshal.StringToCoTaskMemUni(id);
            }
            var ok = store.SetValue(ref AppUserModelIdKey, ref value) == 0 && store.Commit() == 0;
            return ok;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            PropVariantClear(ref value);
            Marshal.ReleaseComObject(store);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);
}
