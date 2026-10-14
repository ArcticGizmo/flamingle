using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Flamingle.App.Native;

/// <summary>
/// Windows 11's own snap-layout switches (Settings → System → Multitasking → Snap windows), which get in the way
/// of Flamingle's zones and layout tabs at the top of the screen. These are the user's Windows settings, not
/// Flamingle's: we read and write them where Windows keeps them (per user, under Explorer\Advanced), so the
/// Settings app agrees with the tray menu. A missing value means "on" (the Windows default).
/// </summary>
internal static class WindowsSnapSettings
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>"Show snap layouts when I drag a window to the top of my screen".</summary>
    public const string SnapBar = "EnableSnapBar";

    /// <summary>"Show snap layouts when I hover over a window's maximize button".</summary>
    public const string MaximiseFlyout = "EnableSnapAssistFlyout";

    public static bool IsOn(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return key?.GetValue(name) is not int value || value != 0;
    }

    /// <summary>Flip a switch and tell running apps (Explorer) that settings changed. The broadcast runs off the
    /// UI thread — it waits on every top-level window, and a hung one mustn't stall us.</summary>
    public static void Set(string name, bool on)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(AdvancedKey))
            key.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);

        Task.Run(() => SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "TraySettings",
            SMTO_ABORTIFHUNG, 1000, out _));
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A;
    private const uint SMTO_ABORTIFHUNG = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);
}
