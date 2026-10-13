using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Flamingle.App.Native;
using Flamingle.App.Services;
using Flamingle.App.Views;
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
    private TaskbarService? _taskbar;
    private HotkeyService? _hotkeys;
    private TrayIcon? _tray;
    private PickerWindow? _picker;

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
            _containers.LayoutPicked += ApplyLayout;
            _containers.GapsToggled += SetGaps;
            _containers.MinimiseRequested += device =>
            {
                if (!_controller.MinimiseZoneFlock(device))
                    ToastWindow.Show("This layout has no flock — turn on \"Zoned windows form a flock\" in the tray.");
            };
            _containers.Start();

            _zones = new ZoneDragService(_hooks, () => _settings, _containers);
            _zones.Start();

            _taskbar = new TaskbarService(_registry, _controller, _hooks, () => _settings);
            _taskbar.Sync();

            // Out-of-context hooks deliver on this (UI) thread's message loop.
            _hooks.Start();

            _hotkeys = new HotkeyService();
            _hotkeys.NewFlockRequested += ShowPicker;
            _hotkeys.ToggleRequested += () => _controller.ToggleFocused();
            _hotkeys.SlotRequested += slot => _controller.ToggleSlot(slot);
            var refused = _hotkeys.Apply(_settings);
            if (refused.Count > 0)
                ToastWindow.Show($"Couldn't register {string.Join(", ", refused)} — another app already uses them.");

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

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _tray = new TrayIcon
        {
            Icon = TrayIconArt.Create(AppProfile.IsDev),
            ToolTipText = $"Flamingle{AppProfile.DisplaySuffix}",
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowPicker();
        TrayIcon.SetIcons(this, [_tray]);
        RebuildMenu(desktop);
    }

    private void RebuildMenu(IClassicDesktopStyleApplicationLifetime? desktop = null)
    {
        if (_tray is null || _controller is null) return;
        desktop ??= ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

        var menu = new NativeMenu();
        menu.Add(new NativeMenuItem($"Flamingle{AppProfile.DisplaySuffix} - {Version}") { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());

        if (_registry.Groups.Count == 0)
            menu.Add(new NativeMenuItem("No flocks yet") { IsEnabled = false });

        foreach (var group in _registry.Groups)
            menu.Add(BuildGroupItem(group));

        menu.Add(new NativeMenuItemSeparator());

        var create = new NativeMenuItem(WithHotkey("New flock…", _settings.NewFlockHotkey));
        create.Click += (_, _) => ShowPicker();
        menu.Add(create);

        menu.Add(BuildZonesItem());
        menu.Add(BuildExperimentsItem());

        var openData = new NativeMenuItem("Open data folder");
        openData.Click += (_, _) => OpenDataFolder();
        menu.Add(openData);

        menu.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("Quit Flamingle");
        quit.Click += (_, _) => desktop?.Shutdown();
        menu.Add(quit);

        _tray.Menu = menu;
        _tray.ToolTipText = _registry.Groups.Count switch
        {
            0 => $"Flamingle{AppProfile.DisplaySuffix}",
            1 => $"Flamingle{AppProfile.DisplaySuffix} — 1 flock",
            var n => $"Flamingle{AppProfile.DisplaySuffix} — {n} flocks",
        };
    }

    private NativeMenuItem BuildGroupItem(Flock group)
    {
        var slot = _registry.SlotOf(group);
        var sub = new NativeMenu();

        var toggle = new NativeMenuItem(WithHotkey("Minimise / restore", HotkeyService.SlotHotkeyText(_settings, slot)));
        toggle.Click += (_, _) => _controller?.Toggle(group);
        sub.Add(toggle);
        sub.Add(new NativeMenuItemSeparator());

        foreach (var m in group.Members)
            sub.Add(new NativeMenuItem(Shorten($"{m.ProcessName} — {m.Title}", 60)) { IsEnabled = false });

        sub.Add(new NativeMenuItemSeparator());
        var dissolve = new NativeMenuItem("Dissolve");
        dissolve.Click += (_, _) => _registry.Dissolve(group);
        sub.Add(dissolve);

        return new NativeMenuItem($"{slot}. {group.Name} ({group.Members.Count})") { Menu = sub };
    }

    private NativeMenuItem BuildZonesItem()
    {
        var sub = new NativeMenu();

        var modifier = string.Equals(_settings.ZoneDragModifier, "none", StringComparison.OrdinalIgnoreCase)
            ? "on every drag"
            : $"hold {_settings.ZoneDragModifier} while dragging";
        var enabled = new NativeMenuItem($"Snap to zones ({modifier})")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.ZonesEnabled,
        };
        enabled.Click += (_, _) => UpdateSettings(_settings with { ZonesEnabled = !_settings.ZonesEnabled });
        sub.Add(enabled);

        var flock = new NativeMenuItem("Zoned windows form a flock")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.ZonesFormFlock,
        };
        flock.Click += (_, _) => UpdateSettings(_settings with { ZonesFormFlock = !_settings.ZonesFormFlock });
        sub.Add(flock);

        var perDesktop = new NativeMenuItem("A separate zone flock on each virtual desktop")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.ZoneFlockPerDesktop,
            IsEnabled = _settings.ZonesFormFlock,
        };
        perDesktop.Click += (_, _) => UpdateSettings(_settings with { ZoneFlockPerDesktop = !_settings.ZoneFlockPerDesktop });
        sub.Add(perDesktop);

        var gaps = new NativeMenuItem($"Gaps between windows ({_settings.ZoneSpacing} px)")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.ZoneGaps,
        };
        gaps.Click += (_, _) => SetGaps(!_settings.ZoneGaps);
        sub.Add(gaps);

        sub.Add(new NativeMenuItemSeparator());

        // One layout picker per monitor. Picking a layout flashes it on that monitor.
        foreach (var monitor in Monitors.All())
        {
            var current = ZoneLayout.ById(_containers?.LayoutIdFor(monitor) ?? _settings.LayoutIdFor(monitor.DeviceName)).Id;
            var layouts = new NativeMenu();
            foreach (var layout in ZoneLayout.Templates)
            {
                var item = new NativeMenuItem(layout.Name)
                {
                    ToggleType = MenuItemToggleType.Radio,
                    IsChecked = layout.Id == current,
                };
                item.Click += (_, _) => ApplyLayout(monitor, layout.Id);
                layouts.Add(item);
            }

            var show = new NativeMenuItem("Show zones");
            show.Click += (_, _) => _zones?.Preview(monitor);
            var fill = new NativeMenuItem("Fill with open windows");
            fill.Click += (_, _) => _containers?.FillWithOpenWindows(monitor);
            layouts.Add(new NativeMenuItemSeparator());
            layouts.Add(show);
            layouts.Add(fill);

            sub.Add(new NativeMenuItem(monitor.Label) { Menu = layouts });
        }

        return new NativeMenuItem("Zones") { Menu = sub };
    }

    private NativeMenuItem BuildExperimentsItem()
    {
        var sub = new NativeMenu();

        var linked = new NativeMenuItem("Linked minimise (one member takes the rest)")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.LinkedMinimise,
        };
        linked.Click += (_, _) => UpdateSettings(_settings with { LinkedMinimise = !_settings.LinkedMinimise });
        sub.Add(linked);

        var correct = new NativeMenuItem("Put windows back exactly after restore")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.CorrectPositionsOnRestore,
        };
        correct.Click += (_, _) =>
            UpdateSettings(_settings with { CorrectPositionsOnRestore = !_settings.CorrectPositionsOnRestore });
        sub.Add(correct);

        var restore = new NativeMenuItem("Restore size as soon as a window is pulled out of a layout")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.RestoreSizeDuringDrag,
        };
        restore.Click += (_, _) => UpdateSettings(_settings with { RestoreSizeDuringDrag = !_settings.RestoreSizeDuringDrag });
        sub.Add(restore);

        var linkedResize = new NativeMenuItem("Resizing a zoned window moves its neighbours")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.LinkedResize,
        };
        linkedResize.Click += (_, _) => UpdateSettings(_settings with { LinkedResize = !_settings.LinkedResize });
        sub.Add(linkedResize);

        var taskbar = new NativeMenuItem("Taskbar button for each flock")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.TaskbarButtons,
        };
        taskbar.Click += (_, _) => UpdateSettings(_settings with { TaskbarButtons = !_settings.TaskbarButtons });
        sub.Add(taskbar);

        var follow = new NativeMenuItem("Flock's taskbar button follows it across virtual desktops")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.TaskbarButtonFollowsDesktop,
            IsEnabled = _settings.TaskbarButtons,
        };
        follow.Click += (_, _) =>
            UpdateSettings(_settings with { TaskbarButtonFollowsDesktop = !_settings.TaskbarButtonFollowsDesktop });
        sub.Add(follow);

        return new NativeMenuItem("Experiments") { Menu = sub };
    }

    /// <summary>Gaps on/off (from the tray or a layout tab): every layout re-flows straight away.</summary>
    private void SetGaps(bool on)
    {
        if (_settings.ZoneGaps == on) return;
        UpdateSettings(_settings with { ZoneGaps = on });
        _containers?.ReflowAll();
    }

    /// <summary>Pick a monitor's layout (from the tray or a layout chip): remember it, re-flow the windows
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

        // Seed the snap-group guess from the window the user was in before they hit the hotkey / tray.
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

    private void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        _settingsFile?.Save(settings);
        RebuildMenu();
        _taskbar?.Sync();
    }

    private void OnRegistryChanged()
    {
        SaveFlocks();
        RebuildMenu();
        _taskbar?.Sync();
    }

    private void SaveFlocks() => _flocksFile?.Save(FlockSnapshot.From(_registry));

    private static void OpenDataFolder()
    {
        var dir = AppProfile.DataDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static string WithHotkey(string label, string? hotkey)
        => string.IsNullOrWhiteSpace(hotkey) ? label : $"{label}   ({hotkey})";

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
