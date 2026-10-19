using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.App.Services;
using Flamingle.App.Views;
using Flamingle.App.Views.Settings;
using Flamingle.Core;
using Flamingle.Core.Changelog;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Persistence;
using Flamingle.Core.Saved;
using Flamingle.Core.Settings;
using Flamingle.Core.Zones;

namespace Flamingle.App;

public partial class App : Application
{
    private readonly FlockRegistry _registry = new();
    private readonly WinEventHooks _hooks = new();
    private JsonFile<AppSettings>? _settingsFile;
    private JsonFile<FlockSnapshot>? _flocksFile;
    private AppSettings _settings = AppSettings.Default;
    private FlockController? _controller;
    private ZoneDragService? _zones;
    private ZoneContainerService? _containers;
    private SavedFlockService? _saved;
    private RelaunchService? _relaunch;
    private FlockDesignService? _design;
    private TaskbarService? _taskbar;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private PickerWindow? _picker;
    private SettingsWindow? _settingsWindow;
    private ChangelogWindow? _changelogWindow;

    private static string Version
    {
        get
        {
            var v = typeof(App).Assembly.GetName().Version;
            return v is null ? "?" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Tray-resident: no main window, so closing the picker mustn't exit the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Carries settings and flocks over from the old Gaggle data folder on first run.
            LegacyData.Migrate(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppProfile.IsDev);

            var dataDir = AppProfile.DataDirectory();
            _settingsFile = new JsonFile<AppSettings>(Path.Combine(dataDir, "settings.json"));
            _settings = _settingsFile.Load(AppSettings.Default);
            // First launch after an update: pick the changelog entries newer than the version that last ran here,
            // then stamp this version so they're shown only once. No last-seen version is a fresh install —
            // seeded silently, nothing to show.
            var whatsNew = PendingChangelog(_settings);
            _settings = _settings with { LastSeenVersion = Version };
            // Write the settings back so the file always shows every option (including newly added ones).
            _settingsFile.Save(_settings);

            // Re-attach to flocks from the last run whose windows are still open.
            _flocksFile = new JsonFile<FlockSnapshot>(Path.Combine(dataDir, "flocks.json"));
            _registry.Load(_flocksFile.Load(FlockSnapshot.Empty).ToGroups(WindowQuery.IsAlive));
            _registry.Changed += OnRegistryChanged;
            SaveFlocks();
            AdoptStableMonitorKeys();

            _controller = new FlockController(_registry, _hooks, () => _settings);
            _controller.Notice += ToastWindow.Show;
            _controller.Start();

            _containers = new ZoneContainerService(() => _settings, _hooks, Path.Combine(dataDir, "zones.json"));
            _containers.Notice += ToastWindow.Show;
            _containers.Slotted += (hwnd, monitor) => _controller.JoinZoneFlock(hwnd, monitor);
            _containers.Unslotted += hwnd => _controller.LeaveZoneFlock(hwnd);
            _containers.Rehomed += (from, to) =>
                _registry.RehomeZoneFlocks(from, to.DeviceName, g => FlockController.ZoneFlockName(to.DeviceName, g.ZoneDesktop));
            _containers.Swapped += (a, b) =>
                _registry.SwapZoneFlocks(a.DeviceName, b.DeviceName, g => FlockController.ZoneFlockName(g.ZoneMonitor!, g.ZoneDesktop));
            _containers.LayoutPicked += ApplyLayout;
            _containers.SettingsRequested += () => ShowSettings("zones");
            _containers.MinimiseRequested += device =>
            {
                if (!_controller.MinimiseZoneFlock(device))
                    ToastWindow.Show("This layout has no flock — turn on \"Zoned windows form a flock\" in Settings → Zones.");
            };
            _containers.PanelFlocksFor = device => new PanelFlocks(_controller.CurrentZoneFlock(device)?.Name, _saved?.All ?? []);
            _containers.FlockRenameRequested += (device, name) =>
            {
                if (_controller.CurrentZoneFlock(device) is { } group) _registry.Rename(group, name);
            };
            _containers.FlockSaveRequested += (device, name) =>
            {
                if (_controller.CurrentZoneFlock(device) is { } group) _saved?.Save(group, name);
            };
            _containers.FlockLoadRequested += LoadSavedFlock;
            _containers.Start();

            _zones = new ZoneDragService(_hooks, () => _settings, _containers);
            _zones.Start();

            _saved = new SavedFlockService(Path.Combine(dataDir, "saved-flocks.json"), _containers);
            _saved.Load();
            _saved.Notice += ToastWindow.Show;
            _saved.Changed += () =>
            {
                _settingsWindow?.RefreshFlocks();
                _containers.RefreshPanels();
            };

            _relaunch = new RelaunchService(_containers, _controller, ApplyLayout, FormFlock);
            _relaunch.Notice += ToastWindow.Show;

            var saved = _saved;
            _design = new FlockDesignService(_containers, _hooks, saved.Update, OpenWindows,
                (handle, z) => saved.CaptureWindow((IntPtr)handle, z),
                (flock, isNew) =>
                {
                    ShowSettings("flocks");
                    _settingsWindow?.ShowForm(flock, isNew);
                });
            _design.Notice += ToastWindow.Show;
            // A window dragged in design mode is being added to a saved flock, not snapped into a zone.
            _zones.Paused = () => _design.IsOpen;

            _taskbar = new TaskbarService(_registry, _controller, _hooks, () => _settings);
            _taskbar.Sync();

            // Out-of-context hooks deliver on this (UI) thread's message loop.
            _hooks.Start();

            _hotkeys = new HotkeyService();
            _hotkeys.NewFlockRequested += ShowPicker;
            _hotkeys.ToggleRequested += () => _controller.ToggleFocused();
            _hotkeys.LayoutMenuRequested += () =>
            {
                if (_design?.IsOpen != true) _containers.ToggleEditAtCursor();
            };
            _hotkeys.SlotRequested += slot => _controller.ToggleSlot(slot);
            ApplyHotkeys();

            SetupTray(desktop);

            if (whatsNew is { Count: > 0 })
                Dispatcher.UIThread.Post(() => ShowChangelog(UpdatedSubhead(whatsNew), whatsNew), DispatcherPriority.Background);

            desktop.Exit += (_, _) =>
            {
                _hotkeys?.Dispose();
                _zones?.Dispose();
                _relaunch?.Dispose();
                _containers?.Dispose();
                _taskbar?.Dispose();
                _hooks.Dispose();
                _tray?.Dispose();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyHotkeys()
    {
        if (_hotkeys is null) return;
        var refused = _hotkeys.Apply(_settings);
        if (refused.Count > 0)
            ToastWindow.Show($"Couldn't register {string.Join(", ", refused)} — another app already uses them.");
    }

    /// <summary>The tray holds only the essentials; everything else lives in the settings window, which clicking
    /// the icon opens.</summary>
    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();

        var settings = new NativeMenuItem("Settings…");
        settings.Click += (_, _) => ShowSettings();
        menu.Add(settings);

        var openData = new NativeMenuItem("Open data folder");
        openData.Click += (_, _) => OpenDataFolder();
        menu.Add(openData);

        menu.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("Quit Flamingle");
        quit.Click += (_, _) => desktop.Shutdown();
        menu.Add(quit);

        _tray = new TrayIcon
        {
            Icon = TrayIconArt.Create(AppProfile.IsDev),
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowSettings();
        TrayIcon.SetIcons(this, [_tray]);
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (_tray is null) return;
        _tray.ToolTipText = _registry.Groups.Count switch
        {
            0 => $"Flamingle{AppProfile.DisplaySuffix}",
            1 => $"Flamingle{AppProfile.DisplaySuffix} — 1 flock",
            var n => $"Flamingle{AppProfile.DisplaySuffix} — {n} flocks",
        };
    }

    private void ShowSettings() => ShowSettings(null);

    /// <summary>Open (or bring forward) the settings window, on <paramref name="page"/> if given.</summary>
    private void ShowSettings(string? page)
    {
        if (_controller is null) return;
        if (_settingsWindow is not null)
        {
            if (page is not null) _settingsWindow.Select(page);
            _settingsWindow.Activate();
            return;
        }

        var window = new SettingsWindow(new SettingsHost
        {
            Settings = () => _settings,
            Update = UpdateSettings,
            Registry = _registry,
            ToggleFlock = group => _controller.Toggle(group),
            NewFlock = ShowPicker,
            SavedFlocks = () => _saved?.All ?? [],
            SaveFlock = group => _saved?.Save(group),
            DeleteSavedFlock = id => _saved?.Delete(id),
            UpdateSavedFlock = flock => _saved?.Update(flock),
            DesignSavedFlock = (flock, isNew) => _design?.Open(flock, isNew),
            LoadSavedFlock = AskWhereAndLoad,
            FreeSavedFlockName = name => _saved?.FreeName(name) ?? name,
            OpenWindows = OpenWindows,
            CaptureWindow = (handle, z) => _saved?.CaptureWindow((IntPtr)handle, z),
            LayoutIdFor = monitor => ZoneLayout.ById(_containers?.LayoutIdFor(monitor) ?? _settings.LayoutIdFor(monitor.DeviceName)).Id,
            ApplyLayout = ApplyLayout,
            ShowZones = monitor => _zones?.Preview(monitor),
            FillZones = monitor => _containers?.FillWithOpenWindows(monitor),
            SetGaps = SetGaps,
            SuspendHotkeys = suspend =>
            {
                if (suspend) _hotkeys?.Suspend();
                else ApplyHotkeys();
            },
            OpenDataFolder = OpenDataFolder,
            Version = Version,
            PreviewChangelog = PreviewChangelog,
        }, page ?? "flocks");
        window.Closed += (_, _) => { if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null; };
        _settingsWindow = window;
        window.Show();
    }

    /// <summary>The windows open now, for the designer to add one from.</summary>
    private static IReadOnlyList<WindowChoice> OpenWindows() => WindowQuery.Candidates()
        .Select(w => new WindowChoice(w.Handle, string.IsNullOrEmpty(w.ProcessName) ? "Window" : w.ProcessName, w.Title))
        .ToList();

    /// <summary>
    /// ▶ in Settings: ask which monitor to load onto (every screen offers "Load here", the one it was saved on marked),
    /// then load there. Only asked when there's a choice to make — more than one monitor, and the flock all on one.
    /// </summary>
    private void AskWhereAndLoad(SavedFlock flock)
    {
        var monitors = Monitors.All();
        if (monitors.Count < 2 || flock.Windows.Count == 0 || flock.Windows.Select(w => w.Placement.Monitor).Distinct().Count() != 1)
        {
            Relaunch(flock, null);
            return;
        }
        var first = flock.Windows[0].Placement;
        var savedOn = PlacementMapper.Monitor(first.Monitor, first.WorkArea, monitors).DeviceName;
        LoadTargetWindow.Ask(flock.Name, monitors, savedOn, ZoneOccupant, m => Relaunch(flock, m.DeviceName));
    }

    /// <summary>What's in a monitor's zones now — its zone flock on the desktop being looked at — or null.</summary>
    private string? ZoneOccupant(string device)
        => _controller?.CurrentZoneFlock(device) is { Members.Count: > 0 } g
            ? $"{g.Members.Count} window{(g.Members.Count == 1 ? "" : "s")} in \"{g.Name}\""
            : null;

    /// <summary>A saved flock picked in a layout's panel: load it onto that layout's monitor.</summary>
    private void LoadSavedFlock(string deviceName, Guid id)
    {
        if (_saved?.All.FirstOrDefault(f => f.Id == id) is { } flock) Relaunch(flock, deviceName);
    }

    /// <summary>
    /// Load a saved flock: find or start its apps and put their windows back. Inputs take their defaults for now —
    /// the load prompt that asks for them (and works out computed ones) is the next step.
    /// </summary>
    private void Relaunch(SavedFlock flock, string? ontoDevice)
    {
        if (_relaunch is null) return;
        var resolved = InputValues.Resolve(flock.InputList, new Dictionary<string, string>(), _ => null);
        if (resolved.Failed.Count > 0)
            ToastWindow.Show($"Loading \"{flock.Name}\" with {string.Join(", ", resolved.Failed)} blank — computed inputs aren't worked out yet.");
        _relaunch.Load(flock, resolved.Values, ontoDevice);
    }

    /// <summary>The windows a load placed become a flock with the saved flock's name. If they all went into one
    /// monitor's zones and make up its whole zone flock, that zone flock just takes the name.</summary>
    private void FormFlock(SavedFlock saved, IReadOnlyList<long> handles)
    {
        var groups = handles.Select(_registry.FindByWindow).Distinct().ToList();
        if (groups is [{ ZoneMonitor: not null } zone] && zone.Members.Count == handles.Count)
        {
            _registry.Rename(zone, saved.Name);
            return;
        }
        _registry.Create(saved.Name, handles.Select(h => WindowQuery.ToMember(WindowQuery.Snapshot((IntPtr)h))));
    }

    /// <summary>Gaps on/off (from settings): every layout re-flows straight away.</summary>
    private void SetGaps(bool on)
    {
        if (_settings.ZoneGaps == on) return;
        UpdateSettings(_settings with { ZoneGaps = on });
        _containers?.ReflowAll();
    }

    /// <summary>Pick a monitor's layout (from settings or a layout chip): remember it, and re-flow the windows
    /// already in its zones.</summary>
    private void ApplyLayout(MonitorInfo monitor, string layoutId)
    {
        UpdateSettings(_settings.WithLayout(monitor.DeviceName, layoutId));
        _containers?.SetLayout(monitor, layoutId);
    }

    /// <summary>Per-monitor state used to be keyed by the monitor's GDI name (<c>\\.\DISPLAY3</c>), which it loses
    /// every time it reconnects. Carry what's saved under a connected monitor's GDI name over to its stable key.</summary>
    private void AdoptStableMonitorKeys()
    {
        foreach (var m in Monitors.All())
        {
            if (m.GdiName is not { } gdi || gdi == m.DeviceName) continue;
            if (_settings.ZoneLayouts.TryGetValue(gdi, out var layout) && !_settings.ZoneLayouts.ContainsKey(m.DeviceName))
                UpdateSettings(_settings.WithLayout(m.DeviceName, layout));
            _registry.RehomeZoneFlocks(gdi, m.DeviceName, g => FlockController.ZoneFlockName(m.DeviceName, g.ZoneDesktop));
        }
    }

    private void ShowPicker()
    {
        if (_controller is null) return;
        if (_picker is not null)
        {
            _picker.Activate();
            return;
        }

        // Seed the snap-group guess from the window the user was in before they hit the hotkey (or opened settings).
        var seed = _controller.LastFocus;
        var windows = WindowQuery.Candidates();
        var guess = seed != 0 ? SnapGroupDetector.Detect(seed, windows) : [];
        var rows = windows
            .Select(w => new PickerWindow.Row(w, guess.Contains(w.Handle), _registry.FindByWindow(w.Handle)?.Name))
            .ToList();

        var picker = new PickerWindow(rows, _registry.Groups.Count + 1, fromSnapGuess: seed != 0);
        picker.Confirmed += (name, picked) => _controller.Create(name, picked.Select(WindowQuery.ToMember));
        picker.Closed += (_, _) => { if (ReferenceEquals(_picker, picker)) _picker = null; };
        _picker = picker;
        picker.Show();
    }

    /// <summary>Persist new settings and re-apply whatever they affect.</summary>
    private void UpdateSettings(AppSettings settings)
    {
        var old = _settings;
        _settings = settings;
        _settingsFile?.Save(settings);

        if (old.NewFlockHotkey != settings.NewFlockHotkey || old.ToggleHotkey != settings.ToggleHotkey
            || old.LayoutMenuHotkey != settings.LayoutMenuHotkey || old.SlotHotkeyModifiers != settings.SlotHotkeyModifiers)
            ApplyHotkeys();
        if (old.ZoneSpacing != settings.ZoneSpacing) _containers?.ReflowAll();
        _taskbar?.Sync();
        _settingsWindow?.Refresh();
    }

    private void OnRegistryChanged()
    {
        SaveFlocks();
        UpdateTrayTooltip();
        _taskbar?.Sync();
        _settingsWindow?.RefreshFlocks();
        _containers?.RefreshPanels();
    }

    private void SaveFlocks() => _flocksFile?.Save(FlockSnapshot.From(_registry));

    /// <summary>The changelog sections to show on this launch: none unless the pop-up is on and a different version
    /// ran here last — then only the releases in between.</summary>
    private static IReadOnlyList<ChangelogSection>? PendingChangelog(AppSettings settings)
    {
        if (!settings.ShowChangelogOnUpdate || string.IsNullOrWhiteSpace(settings.LastSeenVersion)) return null;
        if (settings.LastSeenVersion == Version || ChangelogMarkdown.LoadEmbedded() is not { } markdown) return null;
        return ChangelogParser.UnseenSince(markdown, settings.LastSeenVersion, Version);
    }

    private static string UpdatedSubhead(IReadOnlyList<ChangelogSection> sections) => sections.Count == 1
        ? $"Updated to {sections[0].Display}."
        : $"Updated to {sections[0].Display} — {sections.Count} releases since {sections[^1].Display}.";

    private void ShowChangelog(string subhead, IReadOnlyList<ChangelogSection> sections)
    {
        _changelogWindow?.Close();
        var window = new ChangelogWindow(subhead, sections,
            onSuppress: () => UpdateSettings(_settings with { ShowChangelogOnUpdate = false }));
        window.Closed += (_, _) => { if (ReferenceEquals(_changelogWindow, window)) _changelogWindow = null; };
        _changelogWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>Settings → Changelog (Debug builds): the window an update from one version to another would show,
    /// empty range included.</summary>
    private void PreviewChangelog(string from, string to)
    {
        if (ChangelogMarkdown.LoadEmbedded() is not { } markdown) return;
        var sections = ChangelogParser.UnseenSince(markdown, from, to);
        ShowChangelog(sections.Count == 0
            ? $"Nothing between {from} and {to}."
            : $"Preview: {from} → {to} ({sections.Count} release{(sections.Count == 1 ? "" : "s")}).", sections);
    }

    private static void OpenDataFolder()
    {
        var dir = AppProfile.DataDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
