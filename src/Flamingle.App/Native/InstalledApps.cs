using System.Runtime.InteropServices;

namespace Flamingle.App.Native;

/// <summary>An app from the Start menu's list, as design mode offers it.</summary>
/// <param name="ExePath">For a desktop app, the exe it starts; null for a Store app.</param>
/// <param name="AppUserModelId">For a Store app, the id it's started by.</param>
internal sealed record InstalledApp(string Name, string? ExePath, string Arguments, string? AppUserModelId)
{
    public bool Packaged => ExePath is null;
}

/// <summary>
/// The apps in the Start menu's "All apps" list, read from the shell's Apps folder (the same list Windows shows —
/// desktop shortcuts and Store apps alike). A desktop app is offered by the exe its shortcut starts, a Store app by
/// its AppUserModelID. Entries that are neither (web links, consoles like <c>.msc</c>) are left out. Read once, on a
/// background STA thread, and cached.
/// </summary>
internal static class InstalledApps
{
    private static Task<IReadOnlyList<InstalledApp>>? _all;

    /// <summary>Every app, sorted by name (read the first time it's asked for).</summary>
    public static Task<IReadOnlyList<InstalledApp>> All() => _all ??= Read();

    private static Task<IReadOnlyList<InstalledApp>> Read()
    {
        var done = new TaskCompletionSource<IReadOnlyList<InstalledApp>>();
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Enumerate());
            }
            catch (Exception e) when (e is COMException or InvalidCastException or ArgumentException)
            {
                done.SetResult([]);
            }
        })
        {
            IsBackground = true,
            Name = "Flamingle installed apps",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static IReadOnlyList<InstalledApp> Enumerate()
    {
        var iidItem = IID_IShellItem;
        if (SHCreateItemFromParsingName(AppsFolder, IntPtr.Zero, ref iidItem, out var folder) != 0 || folder is null) return [];

        var apps = new List<InstalledApp>();
        try
        {
            var bhid = BHID_EnumItems;
            var iidEnum = IID_IEnumShellItems;
            if (folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var enumPtr) != 0 || enumPtr == IntPtr.Zero) return [];
            var items = (IEnumShellItems)Marshal.GetObjectForIUnknown(enumPtr);
            Marshal.Release(enumPtr);
            try
            {
                while (items.Next(1, out var item, out var fetched) == 0 && fetched == 1 && item is not null)
                {
                    try
                    {
                        if (ToApp(item) is { } app) apps.Add(app);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(item);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(items);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(folder);
        }

        return apps
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static InstalledApp? ToApp(IShellItem item)
    {
        var name = DisplayName(item, SIGDN_NORMALDISPLAY);
        var id = DisplayName(item, SIGDN_PARENTRELATIVEPARSING);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) return null;

        if (item is IShellItem2 item2)
        {
            var target = GetString(item2, LinkTargetParsingPath);
            if (IsExe(target)) return new InstalledApp(name, target, GetString(item2, LinkArguments) ?? "", null);
        }

        // Store apps are listed by their AppUserModelID: "<package family>!<app>".
        if (id.Contains('!')) return new InstalledApp(name, null, "", id);

        // Desktop apps without an explicit id are listed as "{known folder}\relative\path.exe".
        if (id.StartsWith('{') && id.IndexOf("}\\", StringComparison.Ordinal) is var end and > 0
            && Guid.TryParse(id[1..end], out var known) && KnownFolder(known) is { } root)
        {
            var exe = Path.Combine(root, id[(end + 2)..]);
            if (IsExe(exe) && File.Exists(exe)) return new InstalledApp(name, exe, "", null);
        }
        return null;
    }

    private static bool IsExe(string? path) => path is not null && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static string? DisplayName(IShellItem item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out var p) != 0 || p == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    private static string? GetString(IShellItem2 item, PROPERTYKEY key)
    {
        if (item.GetString(ref key, out var p) != 0 || p == IntPtr.Zero) return null;
        try
        {
            var s = Marshal.PtrToStringUni(p);
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) != 0 || p == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(p);
        }
        finally
        {
            Marshal.FreeCoTaskMem(p);
        }
    }

    // ---- COM ----

    private const string AppsFolder = "shell:::{4234d49b-0245-4df3-b780-3893943456e1}";
    private const uint SIGDN_NORMALDISPLAY = 0;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;

    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    private static readonly Guid IID_IEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    private static readonly Guid BHID_EnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");

    // System.Link.TargetParsingPath and System.Link.Arguments.
    private static readonly PROPERTYKEY LinkTargetParsingPath = new() { fmtid = new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), pid = 2 };
    private static readonly PROPERTYKEY LinkArguments = new() { fmtid = new Guid("436F2667-14E2-4FEB-B30A-146C53B5B674"), pid = 100 };

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }

    [ComImport]
    [Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2 : IShellItem
    {
        // IShellItem's methods again — COM interop needs the whole vtable, in order.
        [PreserveSig] new int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] new int GetParent(out IShellItem parent);
        [PreserveSig] new int GetDisplayName(uint sigdn, out IntPtr name);
        [PreserveSig] new int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] new int Compare(IShellItem other, uint hint, out int order);

        [PreserveSig] int GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreWithCreateObject(int flags, IntPtr createObject, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyStoreForKeys(IntPtr keys, uint count, int flags, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetPropertyDescriptionList(ref PROPERTYKEY key, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int Update(IntPtr pbc);
        [PreserveSig] int GetProperty(ref PROPERTYKEY key, IntPtr propvar);
        [PreserveSig] int GetCLSID(ref PROPERTYKEY key, out Guid clsid);
        [PreserveSig] int GetFileTime(ref PROPERTYKEY key, out long filetime);
        [PreserveSig] int GetInt32(ref PROPERTYKEY key, out int value);
        [PreserveSig] int GetString(ref PROPERTYKEY key, out IntPtr value);
    }

    [ComImport]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
