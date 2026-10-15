using Flamingle.App.Native;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Persistence;
using Flamingle.Core.Saved;

namespace Flamingle.App.Services;

/// <summary>
/// Saves live flocks as recipes (<c>saved-flocks.json</c>) — per window, what's needed to find or start its app
/// again and put it back where it was. Phase 3 spike, step 1: capture only, so we can see how much real apps give
/// away before building matching and relaunching on top.
/// </summary>
internal sealed class SavedFlockService(string path, ZoneContainerService zones)
{
    private const string UwpFrameClass = "ApplicationFrameWindow";
    private const string UwpCoreClass = "Windows.UI.Core.CoreWindow";

    private readonly JsonFile<SavedFlocks> _file = new(path);
    private SavedFlocks _saved = SavedFlocks.Empty;

    public event Action? Changed;

    public event Action<string>? Notice;

    public IReadOnlyList<SavedFlock> All => _saved.Flocks;

    public void Load() => _saved = _file.Load(SavedFlocks.Empty);

    /// <summary>Snapshot a live flock under <paramref name="name"/> (default: the flock's own), replacing any saved
    /// flock of that name.</summary>
    public void Save(Flock group, string? name = null)
    {
        name = string.IsNullOrWhiteSpace(name) ? group.Name : name.Trim();
        var members = group.Members.Where(WindowQuery.IsAlive).Select(m => (IntPtr)m.Handle).ToList();
        if (members.Count == 0)
        {
            Notice?.Invoke($"\"{group.Name}\" has no open windows to save.");
            return;
        }

        var order = ZOrder.Sort(members.Select(h => (long)h), WindowQuery.ZOrderTopFirst());
        var monitors = Monitors.All();
        var windows = order.Select((h, z) => Capture((IntPtr)h, z, monitors)).ToList();

        _saved = _saved.Save(new SavedFlock(Guid.NewGuid(), name, DateTimeOffset.Now, windows), out var replaced);
        if (!_file.Save(_saved))
        {
            Notice?.Invoke($"Couldn't write {Path.GetFileName(_file.Path)}.");
            return;
        }
        Changed?.Invoke();

        var gaps = windows.Count(w => w.Elevated || (w.ExePath is null && !w.Packaged));
        Notice?.Invoke($"{(replaced ? "Updated" : "Saved")} \"{name}\" ({windows.Count} window{(windows.Count == 1 ? "" : "s")})."
                       + (gaps > 0 ? $" {gaps} may not relaunch cleanly — see Settings → Flocks." : ""));
    }

    public void Delete(Guid id)
    {
        _saved = _saved.Remove(id);
        _file.Save(_saved);
        Changed?.Invoke();
    }

    private SavedWindow Capture(IntPtr hwnd, int zIndex, IReadOnlyList<MonitorInfo> monitors)
    {
        var snapshot = WindowQuery.Snapshot(hwnd);
        var pid = AppProcess(hwnd, snapshot.ProcessId);

        var exe = ProcessInfo.ImagePath(pid);
        var packagedId = ProcessInfo.PackagedAppId(pid);
        var commandLine = ProcessInfo.CommandLine(pid);

        return new SavedWindow(
            ProcessName: exe is null ? snapshot.ProcessName : Path.GetFileNameWithoutExtension(exe),
            ExePath: exe,
            Arguments: commandLine is null ? null : CommandLine.Arguments(commandLine),
            AppUserModelId: AppUserModel.GetId(hwnd) ?? packagedId,
            Packaged: packagedId is not null,
            ClassName: WindowQuery.ClassName(hwnd),
            Title: snapshot.Title,
            Placement: PlacementOf(hwnd, snapshot, monitors),
            ZIndex: zIndex,
            Desktop: VirtualDesktops.DesktopOf(hwnd),
            Zone: ZoneOf(hwnd),
            Elevated: ProcessInfo.IsElevated(pid) ?? (commandLine is null));
    }

    /// <summary>The process that really owns the window. A Store app's window is a frame owned by
    /// <c>ApplicationFrameHost</c>; the app itself owns the core window inside it. (A minimised Store app's core
    /// window can be detached from the frame, in which case we're left with the host.)</summary>
    private static uint AppProcess(IntPtr hwnd, uint pid)
    {
        if (WindowQuery.ClassName(hwnd) != UwpFrameClass) return pid;
        var core = Win32.FindWindowExW(hwnd, IntPtr.Zero, UwpCoreClass, null);
        if (core == IntPtr.Zero) return pid;
        Win32.GetWindowThreadProcessId(core, out var appPid);
        return appPid;
    }

    private static SavedPlacement PlacementOf(IntPtr hwnd, Core.Windows.WindowSnapshot snapshot, IReadOnlyList<MonitorInfo> monitors)
    {
        // MonitorFromWindow uses the restored rect for a minimised window, so this is where it will come back.
        var monitor = monitors.FirstOrDefault(m => m.Handle == snapshot.Monitor) ?? monitors.First(m => m.IsPrimary);
        var minimised = snapshot.IsMinimised;
        var maximised = snapshot.IsMaximised || (minimised && WindowQuery.RestoresMaximised(hwnd));
        var onScreen = !minimised && !maximised;

        var restored = (onScreen ? WindowQuery.WindowRect(hwnd) : null) ?? WindowQuery.RestoredRect(hwnd) ?? snapshot.Bounds;
        return new SavedPlacement(
            monitor.DeviceName, monitor.WorkArea, monitor.Scale, restored,
            onScreen ? snapshot.Bounds : null, maximised, minimised);
    }

    private SavedZone? ZoneOf(IntPtr hwnd)
        => zones.ContainerOf(hwnd) is { } c && c.Slots.TryGetValue(hwnd, out var slot)
            ? new SavedZone(c.LayoutId, [.. slot.Zones])
            : null;
}
