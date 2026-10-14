using System.Runtime.InteropServices;

namespace Flamingle.App.Native;

/// <summary>
/// What a taskbar button shows when hovered. By default DWM draws a live picture of the window — for a flock's
/// off-screen 1×1 proxy that's nothing. With "iconic" representation turned on, DWM instead sends the window
/// <see cref="WM_DWMSENDICONICTHUMBNAIL"/> whenever it needs a preview, and we hand it a bitmap we drew
/// (<see cref="SetThumbnail"/>). Also: the thumbnail toolbar (buttons under the preview) and its tooltip, via
/// <c>ITaskbarList3</c>.
/// </summary>
internal static class TaskbarThumbnails
{
    public const uint WM_COMMAND = 0x0111;
    public const uint WM_DWMSENDICONICTHUMBNAIL = 0x0323;
    public const int THBN_CLICKED = 0x1800;

    private const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7;
    private const int DWMWA_HAS_ICONIC_BITMAP = 10;
    private const int DWMWA_DISALLOW_PEEK = 11;

    /// <summary>Sent once the window's taskbar button exists — thumbnail buttons can only be added after it.</summary>
    public static readonly uint WM_TASKBARBUTTONCREATED = RegisterWindowMessageW("TaskbarButtonCreated");

    /// <summary>Turn our own drawn previews on (or back to DWM's default) for a window. Peek (hovering the
    /// preview to see the window in place) is switched off with them — a proxy has nothing to peek at.</summary>
    public static void UseCustomPreview(IntPtr hwnd, bool on)
    {
        if (hwnd == IntPtr.Zero) return;
        var value = on ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, ref value, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP, ref value, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_DISALLOW_PEEK, ref value, sizeof(int));
    }

    /// <summary>Forget the preview we gave DWM, so it asks again the next time one is needed.</summary>
    public static void Invalidate(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero) DwmInvalidateIconicBitmaps(hwnd);
    }

    /// <summary>Give DWM the preview: <paramref name="bgra"/> is premultiplied BGRA, top row first.</summary>
    public static void SetThumbnail(IntPtr hwnd, int width, int height, byte[] bgra)
    {
        var bitmap = Gdi.CreateDib(width, height, bgra);
        if (bitmap == IntPtr.Zero) return;
        try { DwmSetIconicThumbnail(hwnd, bitmap, 0); }
        finally { Gdi.DeleteObject(bitmap); }
    }

    // ---- Thumbnail toolbar ----

    public sealed record ThumbButton(uint Id, IntPtr Icon, string Tip);

    private static ITaskbarList3? _taskbar;

    private static ITaskbarList3? Taskbar
    {
        get
        {
            if (_taskbar is not null) return _taskbar;
            try
            {
                var list = (ITaskbarList3)new TaskbarListClass();
                if (list.HrInit() != 0) return null;
                return _taskbar = list;
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

    /// <summary>Add the buttons under the preview. Only once per window (after <see cref="WM_TASKBARBUTTONCREATED"/>);
    /// afterwards use <see cref="ShowButtons"/>.</summary>
    public static void AddButtons(IntPtr hwnd, IReadOnlyList<ThumbButton> buttons, bool visible)
        => Taskbar?.ThumbBarAddButtons(hwnd, (uint)buttons.Count, ToNative(buttons, visible));

    public static void ShowButtons(IntPtr hwnd, IReadOnlyList<ThumbButton> buttons, bool visible)
        => Taskbar?.ThumbBarUpdateButtons(hwnd, (uint)buttons.Count, ToNative(buttons, visible));

    public static void SetTooltip(IntPtr hwnd, string? tip) => Taskbar?.SetThumbnailTooltip(hwnd, tip);

    private static THUMBBUTTON[] ToNative(IReadOnlyList<ThumbButton> buttons, bool visible)
        => buttons.Select(b => new THUMBBUTTON
        {
            dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS,
            iId = b.Id,
            hIcon = b.Icon,
            szTip = b.Tip,
            dwFlags = visible ? THBF_ENABLED : THBF_HIDDEN,
        }).ToArray();

    private const uint THB_ICON = 0x2;
    private const uint THB_TOOLTIP = 0x4;
    private const uint THB_FLAGS = 0x8;
    private const uint THBF_ENABLED = 0x0;
    private const uint THBF_HIDDEN = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
        public uint dwFlags;
    }

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListClass;

    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(IntPtr hwnd);
        [PreserveSig] int DeleteTab(IntPtr hwnd);
        [PreserveSig] int ActivateTab(IntPtr hwnd);
        [PreserveSig] int SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        [PreserveSig] int MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

        // ITaskbarList3
        [PreserveSig] int SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(IntPtr hwnd, int flags);
        [PreserveSig] int RegisterTab(IntPtr tab, IntPtr main);
        [PreserveSig] int UnregisterTab(IntPtr tab);
        [PreserveSig] int SetTabOrder(IntPtr tab, IntPtr insertBefore);
        [PreserveSig] int SetTabActive(IntPtr tab, IntPtr main, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        [PreserveSig] int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
        [PreserveSig] int SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? tip);
        [PreserveSig] int SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr bitmap, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
}
