using System.Runtime.InteropServices;

namespace Flamingle.App.Native;

/// <summary>
/// Windows notifications the old way: a tray balloon (<c>Shell_NotifyIcon</c> + <c>NIF_INFO</c>), which Windows 10 / 11
/// show as a native notification that lands in the Notification Center — no WinRT, no app registration. Avalonia's
/// <c>TrayIcon</c> doesn't expose balloons, so this owns a second, hidden notify icon purely to send them from.
/// Clicking a notification runs the action it was shown with. Create it on the UI thread.
/// </summary>
internal sealed class TrayNotifier : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1;   // WM_APP + 1
    private const uint IconId = 1;

    private readonly MessageWindow _window = new("Flamingle.TrayNotifier");
    private readonly IntPtr _icon;
    private readonly bool _added;
    private Action? _onClick;

    public TrayNotifier()
    {
        _icon = ExtractIconExW(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out var small, 1) > 0 ? small : IntPtr.Zero;

        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_STATE);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _icon;
        data.dwState = NIS_HIDDEN;
        data.dwStateMask = NIS_HIDDEN;
        _added = Shell_NotifyIconW(NIM_ADD, ref data);

        // Version 4: the callback's lParam carries the event (NIN_*) in its low word.
        data.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
        if (_added) Shell_NotifyIconW(NIM_SETVERSION, ref data);

        _window.AppMessage += OnMessage;
    }

    /// <summary>False if Windows refused the notify icon, so <see cref="Show"/> can't work.</summary>
    public bool IsAvailable => _added;

    /// <summary>Windows put a notification on screen (NIN_BALLOONSHOW).</summary>
    public event Action? Shown;

    /// <summary>Show a notification; <paramref name="onClick"/> runs if it's clicked (only the latest one is live).
    /// Returns false if Windows wouldn't take it.</summary>
    public bool Show(string title, string text, bool warning = false, Action? onClick = null)
    {
        if (!IsAvailable) return false;
        _onClick = onClick;

        var data = Data(NIF_INFO);
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(text, 255);
        data.dwInfoFlags = warning ? NIIF_WARNING : NIIF_INFO;
        return Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != CallbackMessage) return;
        switch ((uint)lParam & 0xFFFF)
        {
            case NIN_BALLOONSHOW:
                Shown?.Invoke();
                break;
            case NIN_BALLOONUSERCLICK:
                var click = _onClick;
                _onClick = null;
                click?.Invoke();
                break;
            case NIN_BALLOONTIMEOUT:
            case NIN_BALLOONHIDE:
                _onClick = null;
                break;
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = Data(0);
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }
        if (_icon != IntPtr.Zero) Gdi.DestroyIcon(_icon);
        _window.Dispose();
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_STATE = 0x08, NIF_INFO = 0x10;
    private const uint NIS_HIDDEN = 0x01;
    private const uint NIIF_INFO = 0x01, NIIF_WARNING = 0x02;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIN_BALLOONSHOW = 0x0402, NIN_BALLOONHIDE = 0x0403, NIN_BALLOONTIMEOUT = 0x0404,
        NIN_BALLOONUSERCLICK = 0x0405;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconExW(string file, int index, IntPtr large, out IntPtr small, uint icons);
}
