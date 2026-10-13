using Flamingle.Core;

namespace Flamingle.App.Services;

/// <summary>
/// A plain append-only log at <c>%APPDATA%\Flamingle (Dev)\debug.log</c> for the experiments whose behaviour
/// depends on what Windows does at runtime (drag loops, z-order) — the user tries it, we read the log.
/// Dev builds only; best effort, never throws.
/// </summary>
internal static class DebugLog
{
    private static readonly string FilePath = Path.Combine(AppProfile.DataDirectory(), "debug.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        if (!AppProfile.IsDev) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
