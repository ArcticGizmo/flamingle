using Avalonia.Threading;
using Velopack;
using Velopack.Sources;

namespace Flamingle.App.Services;

/// <summary>Where an update check has got to, for the tray item and Settings → About.</summary>
/// <param name="Text">One line describing it ("Checking for updates…", "v0.1.2 is ready — restart to install.").</param>
/// <param name="Busy">A check or download is running.</param>
/// <param name="ReadyVersion">The version downloaded and waiting for a restart, if any.</param>
internal sealed record UpdateStatus(string Text, bool Busy, string? ReadyVersion);

/// <summary>
/// On-demand updates from the GitHub releases the release workflow publishes (Velopack's feed rides along with the
/// installer). Checking downloads any newer release straight away; installing it is a separate, explicit restart.
/// Only an installed Flamingle can update — a dev build run from bin\ says so instead.
/// </summary>
internal sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/ArcticGizmo/flamingle";

    private readonly UpdateManager? _manager;
    private VelopackAsset? _ready;

    public UpdateService()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
            if (manager.IsInstalled) _manager = manager;
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Updates unavailable: {ex.Message}");
        }

        // A download from an earlier session that was never installed.
        _ready = _manager?.UpdatePendingRestart;
        Status = _ready is { } pending
            ? Ready(pending)
            : new UpdateStatus(_manager is null ? "Updates work only in an installed Flamingle." : "", false, null);
    }

    public UpdateStatus Status { get; private set; }

    /// <summary>The status moved on. Raised on the UI thread.</summary>
    public event Action? Changed;

    /// <summary>Something worth a toast (up to date, downloaded, failed).</summary>
    public event Action<string>? Notice;

    /// <summary>Velopack is waiting for this process to exit so it can install and relaunch: shut down cleanly.</summary>
    public event Action? ExitRequested;

    /// <summary>Look for a newer release and download it. Does nothing while a check is already running.</summary>
    public async void Check()
    {
        if (Status.Busy) return;
        if (_manager is null)
        {
            Notice?.Invoke("Updates work only in an installed Flamingle — this one isn't.");
            return;
        }

        try
        {
            Set(new UpdateStatus("Checking for updates…", true, null));
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                Set(_ready is { } pending ? Ready(pending) : new UpdateStatus($"Up to date (checked {DateTime.Now:t}).", false, null));
                Notice?.Invoke(_ready is null ? "Flamingle is up to date." : Status.Text);
                return;
            }

            var version = "v" + info.TargetFullRelease.Version;
            Set(new UpdateStatus($"Downloading {version}…", true, null));
            var shown = -1;
            await _manager.DownloadUpdatesAsync(info, percent =>
            {
                // Reported per percent from Velopack's worker thread; the status moves in tens.
                var step = percent / 10 * 10;
                if (step == shown) return;
                shown = step;
                Dispatcher.UIThread.Post(() =>
                {
                    if (Status.Busy) Set(new UpdateStatus($"Downloading {version}… {step}%", true, null));
                });
            });

            _ready = info.TargetFullRelease;
            Set(Ready(_ready));
            Notice?.Invoke($"Flamingle {version} downloaded — choose \"Restart to update\" in the tray menu or Settings → About.");
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Update check failed: {ex}");
            Set(_ready is { } pending ? Ready(pending) : new UpdateStatus($"Couldn't check for updates: {ex.Message}", false, null));
            Notice?.Invoke("Couldn't check for updates — see Settings → About.");
        }
    }

    /// <summary>Install the downloaded update: Velopack waits for us to exit, installs it, and starts the new version.</summary>
    public void RestartToUpdate()
    {
        if (_manager is null || _ready is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready, silent: false, restart: true);
            ExitRequested?.Invoke();
        }
        catch (Exception ex)
        {
            DebugLog.Write($"Applying update failed: {ex}");
            Notice?.Invoke($"Couldn't install the update: {ex.Message}");
        }
    }

    private static UpdateStatus Ready(VelopackAsset asset)
        => new($"v{asset.Version} is ready — restart Flamingle to install it.", false, "v" + asset.Version);

    private void Set(UpdateStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }
}
