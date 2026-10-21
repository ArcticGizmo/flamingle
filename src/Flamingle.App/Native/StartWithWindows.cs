using Flamingle.Core;
using Microsoft.Win32;

namespace Flamingle.App.Native;

/// <summary>
/// "Start Flamingle when you sign in": a value under the user's <c>Run</c> key pointing at this exe. Kept in the
/// registry rather than settings.json, so it's what Windows (and Task Manager's Startup apps) actually sees. A dev
/// build registers under its own name, so it never replaces a real install's entry.
/// </summary>
internal static class StartWithWindows
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string ValueName => AppProfile.DataFolderName;

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsOn()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Set(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(ValueName, Command, RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If it's on but points at another exe (the app was moved or reinstalled elsewhere), point it here.</summary>
    public static void Refresh()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is string current && current != Command) Set(true);
    }
}
