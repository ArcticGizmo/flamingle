using System.Runtime.InteropServices;
using System.Text;

namespace Flamingle.App.Native;

/// <summary>
/// What Flamingle can learn about another process from outside it — its exe, command line, whether it's elevated
/// and whether it's a packaged (Store / MSIX) app. Everything opens the process with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> only, and returns null when Windows won't say.
/// </summary>
internal static class ProcessInfo
{
    /// <summary>The full path of the process's exe.</summary>
    public static string? ImagePath(uint pid) => WithProcess(pid, h =>
    {
        var sb = new StringBuilder(1024);
        var size = (uint)sb.Capacity;
        return Win32.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
    });

    /// <summary>The process's full command line (Windows 8.1+). Null for processes we can't query, e.g. elevated
    /// ones when Flamingle isn't.</summary>
    public static string? CommandLine(uint pid) => WithProcess(pid, h =>
    {
        // First ask how big the buffer must be, then fetch: the buffer starts with a UNICODE_STRING whose Buffer
        // points further into the same allocation.
        NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;

        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
            var length = (ushort)Marshal.ReadInt16(buffer);   // bytes
            var text = Marshal.ReadIntPtr(buffer, IntPtr.Size); // after Length, MaximumLength and padding
            return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    });

    /// <summary>True if the process runs elevated (as administrator); null if its token can't be read.</summary>
    public static bool? IsElevated(uint pid) => WithProcess<bool?>(pid, h =>
    {
        if (!OpenProcessToken(h, TOKEN_QUERY, out var token)) return null;
        try
        {
            return GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _) ? elevated != 0 : null;
        }
        finally
        {
            Win32.CloseHandle(token);
        }
    });

    /// <summary>For a packaged app, its AppUserModelID (e.g. <c>Microsoft.WindowsTerminal_8wekyb3d8bbwe!App</c>);
    /// null for an ordinary Win32 process.</summary>
    public static string? PackagedAppId(uint pid) => WithProcess(pid, h =>
    {
        var length = 0u;
        if (GetApplicationUserModelId(h, ref length, null) != ERROR_INSUFFICIENT_BUFFER || length == 0) return null;
        var sb = new StringBuilder((int)length);
        return GetApplicationUserModelId(h, ref length, sb) == 0 ? sb.ToString() : null;
    });

    private static T? WithProcess<T>(uint pid, Func<IntPtr, T?> query)
    {
        var h = Win32.OpenProcess(Win32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return default;
        try
        {
            return query(h);
        }
        finally
        {
            Win32.CloseHandle(h);
        }
    }

    private const int ProcessCommandLineInformation = 60;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetApplicationUserModelId(IntPtr process, ref uint length, StringBuilder? id);
}
