using System.Diagnostics;
using Microsoft.Win32;

namespace Flamingle.App.Native;

/// <summary>
/// Windows 11's own snap-layout switches (Settings → System → Multitasking → Snap windows), which get in the way
/// of Flamingle's zones and layout tabs at the top of the screen. These are the user's Windows settings, not
/// Flamingle's: we read and write them where Windows keeps them (per user, under Explorer\Advanced), so the
/// Settings app agrees with ours. A missing value means "on" (the Windows default).
///
/// <para>Explorer only reads them when it starts (the Settings app has a private way to apply them live), so a
/// change takes effect after <see cref="RestartExplorer"/>, or the next sign-in.</para>
/// </summary>
internal static class WindowsSnapSettings
{
    private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    /// <summary>"Show snap layouts when I drag a window to the top of my screen".</summary>
    public const string SnapBar = "EnableSnapBar";

    /// <summary>"Show snap layouts when I hover over a window's maximize button".</summary>
    public const string MaximiseFlyout = "EnableSnapAssistFlyout";

    // Posted to the taskbar, this asks Explorer to exit cleanly — what Ctrl+Shift+right-click → "Exit Explorer"
    // on the taskbar does. Undocumented but long-standing.
    private const uint WM_EXITEXPLORER = 0x05B4;

    public static bool IsOn(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AdvancedKey);
        return key?.GetValue(name) is not int value || value != 0;
    }

    public static void Set(string name, bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(AdvancedKey);
        key.SetValue(name, on ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Restart Explorer (taskbar, desktop, File Explorer windows) so it re-reads the switches. Asks it to exit
    /// cleanly, waits for it off the UI thread, then starts it again. Returns false if it didn't exit in time
    /// (then nothing is started — a second explorer.exe would just open a folder window).
    /// </summary>
    public static Task<bool> RestartExplorer() => Task.Run(() =>
    {
        var tray = Win32.FindWindowW("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return false;
        Win32.GetWindowThreadProcessId(tray, out var pid);

        using var explorer = Process.GetProcessById((int)pid);
        if (!Win32.PostMessageW(tray, WM_EXITEXPLORER, IntPtr.Zero, IntPtr.Zero)) return false;
        if (!explorer.WaitForExit(TimeSpan.FromSeconds(8))) return false;

        // Windows normally leaves a cleanly-exited shell down; if it brought one back itself, leave that be.
        Thread.Sleep(1000);
        if (Win32.FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero) return true;
        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
        return true;
    });
}
