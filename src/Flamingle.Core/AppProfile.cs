namespace Flamingle.Core;

/// <summary>
/// Isolates a development build from an installed release (mirrors shrike/perch). A dev instance uses a
/// <c>"Flamingle (Dev)"</c> data folder and suffixes process-global names (single-instance mutex, tray
/// tooltip) with <c>" (Dev)"</c>, so <c>run.bat</c> never touches a real install's state.
///
/// <para><see cref="IsDev"/> is <c>true</c> for a Debug build and can be forced either way with the
/// <c>FLAMINGLE_DEV</c> environment variable (<c>0</c>/<c>false</c> turns it off, anything else on).</para>
/// </summary>
public static class AppProfile
{
    public static bool IsDev { get; } = ComputeIsDev();

    /// <summary><c>"Flamingle"</c> for release, <c>"Flamingle (Dev)"</c> for dev.</summary>
    public static string DataFolderName => FolderName(IsDev);

    /// <summary><c>""</c> for release, <c>" (Dev)"</c> for dev.</summary>
    public static string DisplaySuffix => IsDev ? " (Dev)" : "";

    /// <summary><c>%APPDATA%\Flamingle</c> (or the dev folder).</summary>
    public static string DataDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName);

    internal static string FolderName(bool dev) => dev ? "Flamingle (Dev)" : "Flamingle";

    /// <summary>The folder the app used while it was called Gaggle.</summary>
    internal static string LegacyFolderName(bool dev) => dev ? "Gaggle (Dev)" : "Gaggle";

    private static bool ComputeIsDev()
    {
        var env = Environment.GetEnvironmentVariable("FLAMINGLE_DEV");
        if (!string.IsNullOrEmpty(env))
            return !(env == "0" || env.Equals("false", StringComparison.OrdinalIgnoreCase));
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
