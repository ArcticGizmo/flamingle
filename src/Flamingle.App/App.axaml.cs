using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Flamingle.App.Native;
using Flamingle.App.Services;
using Flamingle.App.Views;
using Flamingle.App.Views.Settings;
using Flamingle.Core;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Persistence;
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
    private TaskbarService? _taskbar;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private PickerWindow? _picker;
    private SettingsWindow? _settingsWindow;

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
            // Write the settings back so the file always shows every option (including newly added ones).
            _settingsFile.Save(_settings);

            // Re-attach to flocks from the last run whose windows are still open.
            _flocksFile = new JsonFile<FlockSnapshot>(Path.Combine(dataDir, "flocks.json"));
            _registry.Load(_flocksFile.Load(FlockSnapshot.Empty).ToGroups(WindowQuery.IsAlive));
            _registry.Changed += OnRegistryChanged;
            SaveFlocks();

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

            _taskbar = new TaskbarService(_registry, _controller, _hooks, () => _settings);
            _taskbar.Sync();

            // Out-of-context hooks deliver on this (UI) thread's message loop.
            _hooks.Start();

            _hotkeys = new HotkeyService();
            _hotkeys.NewFlockRequested += ShowPicker;
            _hotkeys.ToggleRequested += () => _controller.ToggleFocused();
            _hotkeys.LayoutMenuRequested += () => _containers.ToggleEditAtCursor();
            _hotkeys.SlotRequested += slot => _controller.ToggleSlot(slot);
            ApplyHotkeys();

            SetupTray(desktop);

            desktop.Exit += (_, _) =>
            {
                _hotkeys?.Dispose();
                _zones?.Dispose();
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
        }, page ?? "flocks");
        window.Closed += (_, _) => { if (ReferenceEquals(_settingsWindow, window)) _settingsWindow = null; };
        _settingsWindow = window;
        window.Show();
    }

    /// <summary>
    /// A saved flock picked in a layout's panel. For now this only switches the layout to the one the flock was saved
    /// in — finding or starting its apps and placing their windows is the next step of save and relaunch.
    /// </summary>
    private void LoadSavedFlock(string deviceName, Guid id)
    {
        if (_saved?.All.FirstOrDefault(f => f.Id == id) is not { } flock) return;
        var layoutId = flock.MainZoneLayout();
        if (layoutId is not null && Monitors.All().FirstOrDefault(m => m.DeviceName == deviceName) is { } monitor)
            ApplyLayout(monitor, layoutId);

        ToastWindow.Show(layoutId is null
            ? $"\"{flock.Name}\" wasn't saved from a zone layout. Starting its apps isn't built yet."
            : $"Switched to \"{flock.Name}\"'s layout ({ZoneLayout.ById(layoutId).Name}). Starting its apps isn't built yet.");
    }

    /// <summary>Gaps on/off (from settings): every layout re-flows straight away.</summary>
    private void SetGaps(bool on)
    {
        if (_settings.ZoneGaps == on) return;
        UpdateSettings(_settings with { ZoneGaps = on });
        _containers?.ReflowAll();
    }

    /// <summary>Pick a monitor's layout (from settings or a layout chip): remember it, re-flow the windows
    /// already in its zones, and flash the new zones.</summary>
    private void ApplyLayout(MonitorInfo monitor, string layoutId)
    {
        UpdateSettings(_settings.WithLayout(monitor.DeviceName, layoutId));
        _containers?.SetLayout(monitor, layoutId);
        _zones?.Preview(monitor);
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

    private static void OpenDataFolder()
    {
        var dir = AppProfile.DataDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }
}
