using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Gaggle.App.Native;
using Gaggle.App.Services;
using Gaggle.App.Views;
using Gaggle.Core;
using Gaggle.Core.Grouping;
using Gaggle.Core.Persistence;
using Gaggle.Core.Settings;

namespace Gaggle.App;

public partial class App : Application
{
    private readonly GaggleRegistry _registry = new();
    private JsonFile<AppSettings>? _settingsFile;
    private JsonFile<GaggleSnapshot>? _gagglesFile;
    private AppSettings _settings = AppSettings.Default;
    private GaggleController? _controller;
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

            var dataDir = AppProfile.DataDirectory();
            _settingsFile = new JsonFile<AppSettings>(Path.Combine(dataDir, "settings.json"));
            _settings = _settingsFile.Load(AppSettings.Default);
            // Write the defaults out so there's a file to edit.
            if (!File.Exists(_settingsFile.Path)) _settingsFile.Save(_settings);

            // Re-attach to gaggles from the last run whose windows are still open.
            _gagglesFile = new JsonFile<GaggleSnapshot>(Path.Combine(dataDir, "gaggles.json"));
            _registry.Load(_gagglesFile.Load(GaggleSnapshot.Empty).ToGroups(WindowQuery.IsAlive));
            _registry.Changed += OnRegistryChanged;
            SaveGaggles();

            _controller = new GaggleController(_registry, () => _settings);
            _controller.Notice += ToastWindow.Show;
            _controller.Start();

            _hotkeys = new HotkeyService();
            _hotkeys.NewGaggleRequested += ShowPicker;
            _hotkeys.ToggleRequested += () => _controller.ToggleFocused();
            _hotkeys.SlotRequested += slot => _controller.ToggleSlot(slot);
            var refused = _hotkeys.Apply(_settings);
            if (refused.Count > 0)
                ToastWindow.Show($"Couldn't register {string.Join(", ", refused)} â€” another app already uses them.");

            SetupTray(desktop);

            desktop.Exit += (_, _) =>
            {
                _hotkeys?.Dispose();
                _controller?.Dispose();
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
            ToolTipText = $"Gaggle{AppProfile.DisplaySuffix}",
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
        menu.Add(new NativeMenuItem($"Gaggle{AppProfile.DisplaySuffix} - {Version}") { IsEnabled = false });
        menu.Add(new NativeMenuItemSeparator());

        if (_registry.Groups.Count == 0)
            menu.Add(new NativeMenuItem("No gaggles yet") { IsEnabled = false });

        foreach (var group in _registry.Groups)
            menu.Add(BuildGroupItem(group));

        menu.Add(new NativeMenuItemSeparator());

        var create = new NativeMenuItem(WithHotkey("New gaggleâ€¦", _settings.NewGaggleHotkey));
        create.Click += (_, _) => ShowPicker();
        menu.Add(create);

        menu.Add(BuildExperimentsItem());

        var openData = new NativeMenuItem("Open data folder");
        openData.Click += (_, _) => OpenDataFolder();
        menu.Add(openData);

        menu.Add(new NativeMenuItemSeparator());

        var quit = new NativeMenuItem("Quit Gaggle");
        quit.Click += (_, _) => desktop?.Shutdown();
        menu.Add(quit);

        _tray.Menu = menu;
        _tray.ToolTipText = _registry.Groups.Count switch
        {
            0 => $"Gaggle{AppProfile.DisplaySuffix}",
            1 => $"Gaggle{AppProfile.DisplaySuffix} â€” 1 gaggle",
            var n => $"Gaggle{AppProfile.DisplaySuffix} â€” {n} gaggles",
        };
    }

    private NativeMenuItem BuildGroupItem(GaggleGroup group)
    {
        var slot = _registry.SlotOf(group);
        var sub = new NativeMenu();

        var toggle = new NativeMenuItem(WithHotkey("Minimise / restore", HotkeyService.SlotHotkeyText(_settings, slot)));
        toggle.Click += (_, _) => _controller?.Toggle(group);
        sub.Add(toggle);
        sub.Add(new NativeMenuItemSeparator());

        foreach (var m in group.Members)
            sub.Add(new NativeMenuItem(Shorten($"{m.ProcessName} â€” {m.Title}", 60)) { IsEnabled = false });

        sub.Add(new NativeMenuItemSeparator());
        var dissolve = new NativeMenuItem("Dissolve");
        dissolve.Click += (_, _) => _registry.Dissolve(group);
        sub.Add(dissolve);

        return new NativeMenuItem($"{slot}. {group.Name} ({group.Members.Count})") { Menu = sub };
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

        return new NativeMenuItem("Experiments") { Menu = sub };
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
    }

    private void OnRegistryChanged()
    {
        SaveGaggles();
        RebuildMenu();
    }

    private void SaveGaggles() => _gagglesFile?.Save(GaggleSnapshot.From(_registry));

    private static void OpenDataFolder()
    {
        var dir = AppProfile.DataDirectory();
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private static string WithHotkey(string label, string? hotkey)
        => string.IsNullOrWhiteSpace(hotkey) ? label : $"{label}   ({hotkey})";

    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "â€¦";
}
