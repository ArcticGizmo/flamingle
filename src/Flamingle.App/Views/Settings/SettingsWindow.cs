using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.Core;
using Flamingle.Core.Geometry;
using Flamingle.Core.Grouping;
using Flamingle.Core.Saved;
using Flamingle.Core.Settings;
using Flamingle.Core.Zones;
using static Flamingle.App.Views.Settings.SettingsUi;

namespace Flamingle.App.Views.Settings;

/// <summary>What the settings window needs from the app: the settings, a way to change them, and the actions
/// its buttons trigger. Changes apply live — the app persists them and re-applies whatever they affect.</summary>
internal sealed class SettingsHost
{
    public required Func<AppSettings> Settings { get; init; }
    public required Action<AppSettings> Update { get; init; }
    public required FlockRegistry Registry { get; init; }
    public required Action<Flock> ToggleFlock { get; init; }
    public required Action NewFlock { get; init; }
    public required Func<IReadOnlyList<SavedFlock>> SavedFlocks { get; init; }
    public required Action<Flock> SaveFlock { get; init; }
    public required Action<Guid> DeleteSavedFlock { get; init; }
    public required Action<SavedFlock> UpdateSavedFlock { get; init; }

    /// <summary>Open design mode on a saved flock (is it new?).</summary>
    public required Action<SavedFlock, bool> DesignSavedFlock { get; init; }
    public required Action<SavedFlock> LoadSavedFlock { get; init; }
    public required Func<string, string> FreeSavedFlockName { get; init; }
    public required Func<IReadOnlyList<WindowChoice>> OpenWindows { get; init; }
    public required Func<long, int, SavedWindow?> CaptureWindow { get; init; }
    public required Func<MonitorInfo, string> LayoutIdFor { get; init; }
    public required Action<MonitorInfo, string> ApplyLayout { get; init; }
    public required Action<MonitorInfo> ShowZones { get; init; }
    public required Action<MonitorInfo> FillZones { get; init; }
    public required Action<bool> SetGaps { get; init; }
    public required Action<bool> SuspendHotkeys { get; init; }
    public required Action OpenDataFolder { get; init; }
    public required string Version { get; init; }

    /// <summary>Pop the "what's new" window for an update from one version to another (the dev-build preview).</summary>
    public required Action<string, string> PreviewChangelog { get; init; }
}

/// <summary>
/// Flamingle's settings, in a window modelled on Perch's: a navigation rail on the left, a scrolling page on the
/// right made of rows (name and description, control on the right). Pages are rebuilt from the current settings
/// whenever they change — from here, the layout tabs or the settings file — so the window never shows stale state.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const double NavWidth = 178;

    // Every page (and the designer) is centred in the content area and grows up to this width.
    private const double PageMaxWidth = 1000;
    private static readonly Thickness PageMargin = new(32, 20, 32, 32);

    private readonly SettingsHost _host;
    private readonly Dictionary<string, (ScrollViewer Page, Action<StackPanel> Build)> _pages = [];
    private readonly List<(string Key, Button Item)> _nav = [];
    private readonly Panel _content = new();

    // The saved-flock designer, shown in place of the Flocks page while a saved flock is being edited.
    private readonly ScrollViewer _editorPage = new()
    {
        IsVisible = false,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private SavedFlockEditor? _editor;

    public SettingsWindow(SettingsHost host, string page = "flocks")
    {
        _host = host;
        Title = $"Flamingle{AppProfile.DisplaySuffix} Settings";
        Icon = TrayIconArt.Product();
        Width = 1080;
        Height = 700;
        MinWidth = 720;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Palette.Panel;

        var nav = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddPage(nav, "flocks", "Flocks", BuildFlocks);
        AddPage(nav, "zones", "Zones", BuildZones);
        AddPage(nav, "snapping", "Windows snapping", BuildWindowsSnapping);
        AddPage(nav, "taskbar", "Taskbar", BuildTaskbar);
        AddPage(nav, "behaviour", "Behaviour", BuildBehaviour);
        AddPage(nav, "shortcuts", "Shortcuts", BuildShortcuts);
        AddPage(nav, "about", "About", BuildAbout);
        AddPage(nav, "changelog", "Changelog", BuildChangelog);
        _content.Children.Add(_editorPage);

        var rail = new Border { Background = Palette.NavBg, Width = NavWidth, Child = nav };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(rail, 0);
        Grid.SetColumn(_content, 1);
        grid.Children.Add(rail);
        grid.Children.Add(_content);
        Content = grid;

        Select(page);
    }

    private AppSettings S => _host.Settings();

    private void Update(Func<AppSettings, AppSettings> change) => _host.Update(change(S));

    /// <summary>Settings changed somewhere: rebuild every page but the flock list.</summary>
    public void Refresh()
    {
        foreach (var key in _pages.Keys.Where(k => k != "flocks")) Rebuild(key);
    }

    /// <summary>Flocks were created, changed or dissolved.</summary>
    public void RefreshFlocks() => Rebuild("flocks");

    public void Select(string key)
    {
        _editor = null;
        _editorPage.Content = null;
        _editorPage.IsVisible = false;
        if (!_pages.ContainsKey(key)) key = "flocks";
        foreach (var (k, (page, _)) in _pages) page.IsVisible = k == key;
        foreach (var (k, item) in _nav)
        {
            var on = k == key;
            item.Background = on ? Palette.ButtonBg : Palette.NavBg;
            item.Foreground = on ? Palette.Title : Palette.Muted;
            item.BorderBrush = on ? Palette.AccentBrush : Brushes.Transparent;
        }
    }

    /// <summary>Edit a saved flock (or a new, not yet stored one): in design mode on screen, or — with that off — in
    /// the form, in place of the Flocks page.</summary>
    private void ShowEditor(SavedFlock flock, bool isNew = false)
    {
        if (S.DesignModeEditing)
        {
            _host.DesignSavedFlock(flock, isNew);
            return;
        }
        ShowForm(flock, isNew);
    }

    /// <summary>The form editor for a saved flock (also where design mode's "Edit as a form" lands).</summary>
    public void ShowForm(SavedFlock flock, bool isNew = false)
    {
        Select("flocks");
        foreach (var (page, _) in _pages.Values) page.IsVisible = false;
        _editor = new SavedFlockEditor(flock, isNew, _host.UpdateSavedFlock, () => Select("flocks"),
            _host.OpenWindows, _host.CaptureWindow);
        _editorPage.Content = new Border
        {
            Margin = PageMargin, MaxWidth = PageMaxWidth, HorizontalAlignment = HorizontalAlignment.Stretch, Child = _editor,
        };
        _editorPage.IsVisible = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.Handled)
        {
            // In the designer Esc goes back to the Flocks page (asking first if there are unsaved changes).
            if (_editor is not null) _editor.Back();
            else Close();
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    private void AddPage(StackPanel nav, string key, string title, Action<StackPanel> build)
    {
        var page = new ScrollViewer
        {
            IsVisible = false,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _pages[key] = (page, build);
        _content.Children.Add(page);
        Rebuild(key);

        var item = new Button
        {
            Content = title,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Palette.NavBg,
            Foreground = Palette.Muted,
            BorderThickness = new Thickness(3, 0, 0, 0),
            BorderBrush = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(13, 0, 8, 0),
            Height = 42,
            FontSize = 14,
        };
        item.Click += (_, _) => Select(key);
        nav.Children.Add(item);
        _nav.Add((key, item));
    }

    private void Rebuild(string key)
    {
        var (page, build) = _pages[key];
        var body = new StackPanel { Margin = PageMargin, MaxWidth = PageMaxWidth, HorizontalAlignment = HorizontalAlignment.Stretch };
        build(body);
        page.Content = body;
    }

    // ---- Flocks ----

    // Which rows are opened up (kept across rebuilds).
    private readonly HashSet<Guid> _openFlocks = [];
    private readonly HashSet<Guid> _openSaved = [];

    /// <summary>
    /// Two compact lists, styled like the saved-flock designer: the flocks open now and the saved ones. A row shows
    /// its name and a one-line summary with its actions as icons; click it to see its windows (and, for an open
    /// flock, rename it).
    /// </summary>
    private void BuildFlocks(StackPanel page)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        header.Children.Add(PageTitle("Flocks"));
        var newFlock = FlatButton("New flock…", _host.NewFlock);
        newFlock.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(newFlock, 1);
        header.Children.Add(newFlock);
        page.Children.Add(header);
        page.Children.Add(BodyText("Windows that minimise and restore together. Save one to bring it back later — apps and all."));

        var groups = _host.Registry.Groups;
        page.Children.Add(ListTitle("Open now", groups.Count));
        page.Children.Add(groups.Count == 0
            ? BodyText($"None open. Press {Hotkey(S.NewFlockHotkey)} in any window to start one, or drop windows into zones.")
            : List(groups.Select(FlockRow)));

        var saved = _host.SavedFlocks();
        page.Children.Add(ListTitle("Saved", saved.Count, FlatButton("New saved flock…", NewSavedFlock)));
        page.Children.Add(saved.Count == 0
            ? BodyText("Nothing saved yet. Save an open flock, or start a new one from the windows you have open.")
            : List(saved.Select(SavedRow)));
    }

    /// <summary>Start a saved flock from scratch in the designer — stored only once its changes are saved.</summary>
    private void NewSavedFlock()
        => ShowEditor(new SavedFlock(Guid.NewGuid(), _host.FreeSavedFlockName("New flock"), DateTimeOffset.Now, []), isNew: true);

    private static Control ListTitle(string text, int count, Control? action = null)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        title.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title });
        if (count > 0)
            title.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 13, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Bottom });

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 22, 0, 8) };
        row.Children.Add(title);
        if (action is not null)
        {
            Grid.SetColumn(action, 1);
            row.Children.Add(action);
        }
        return row;
    }

    private Control FlockRow(Flock group)
    {
        var slot = _host.Registry.SlotOf(group);
        var open = _openFlocks.Contains(group.Id);
        var kind = group.ZoneMonitor is { } device ? $"Zones · {device.TrimStart('\\', '.')}" : "Picked by hand";
        var meta = $"{kind} · {Count(group.Members.Count, "window")}" + (SlotHotkey(slot) is { } hk ? $" · {hk}" : "");

        return ExpandingRow(
            Tile(slot.ToString(), slot - 1),
            RowTitle(group.Name),
            RowMeta(meta),
            open,
            () => ToggleOpen(_openFlocks, group.Id),
            [
                IconButton(Glyphs.MinimiseRestore, "Minimise / restore", () => _host.ToggleFlock(group)),
                IconButton(Glyphs.Save, "Save — to start its apps and put it back later", () => _host.SaveFlock(group)),
                IconButton(Glyphs.Dissolve, "Dissolve — its windows stay open", () => _host.Registry.Dissolve(group)),
            ],
            () =>
            {
                var body = new StackPanel { Spacing = 10 };
                var members = new StackPanel { Spacing = 3 };
                foreach (var m in group.Members)
                    members.Children.Add(WindowLine(string.IsNullOrEmpty(m.ProcessName) ? "Window" : m.ProcessName, m.Title));
                body.Children.Add(members);
                body.Children.Add(RenameField(group));
                return body;
            });
    }

    /// <summary>Rename an open flock: applied on Enter or leaving the box. A renamed zone flock keeps its name.</summary>
    private Control RenameField(Flock group)
    {
        var box = new TextBox { Text = group.Name, FontSize = 13, Width = 280 };
        void Commit()
        {
            if (!string.IsNullOrWhiteSpace(box.Text) && box.Text.Trim() != group.Name) _host.Registry.Rename(group, box.Text);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(new TextBlock { Text = "Name", FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(box);
        return row;
    }

    private Control SavedRow(SavedFlock flock, int index)
    {
        var open = _openSaved.Contains(flock.Id);
        var asks = flock.InputList.Count;
        var meta = new WrapPanel();
        meta.Children.Add(RowMeta(Count(flock.Windows.Count, "window")
                                  + (asks > 0 ? $" · asks for {Count(asks, "value")}" : "")
                                  + $" · saved {flock.SavedAt.LocalDateTime:d MMM, HH:mm}", trailing: 8));
        if (_host.Registry.Groups.Any(g => string.Equals(g.Name, flock.Name, StringComparison.OrdinalIgnoreCase)))
            meta.Children.Add(Pill("open", Palette.ButtonBg, Palette.Fg));
        if (flock.Windows.Any(w => w.IsChanged))
            meta.Children.Add(Pill("edited", Palette.ButtonBg, Palette.Fg));
        var warnings = flock.Windows.Count(Warning);
        if (warnings > 0)
            meta.Children.Add(Pill(warnings == 1 ? "1 may not relaunch" : $"{warnings} may not relaunch", WarnBg, WarnBrush));

        var initial = flock.Name.Trim() is { Length: > 0 } n ? n[..1].ToUpperInvariant() : "?";
        return ExpandingRow(
            Tile(initial, index + 2),
            RowTitle(flock.Name),
            meta,
            open,
            () => ToggleOpen(_openSaved, flock.Id),
            [
                IconButton(Glyphs.Load, "Load — start its apps and put its windows back", () => _host.LoadSavedFlock(flock)),
                IconButton(Glyphs.Edit, "Edit what it starts and asks for", () => ShowEditor(flock)),
                IconButton(Glyphs.Delete, "Delete", () => _host.DeleteSavedFlock(flock.Id)),
            ],
            () =>
            {
                var windows = new StackPanel { Spacing = 8 };
                foreach (var w in flock.Windows.OrderBy(w => w.ZIndex))
                {
                    var line = new StackPanel { Spacing = 1 };
                    line.Children.Add(WindowLine(w.ProcessName, w.Title));
                    var detail = $"{Place(w)} · {Recipe(w)}";
                    var text = new TextBlock
                    {
                        Text = detail, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = Warning(w) ? WarnBrush : Palette.Muted,
                    };
                    ToolTip.SetTip(text, detail);
                    line.Children.Add(text);
                    windows.Children.Add(line);
                }
                return windows;
            });
    }

    private void ToggleOpen(HashSet<Guid> open, Guid id)
    {
        if (!open.Remove(id)) open.Add(id);
        RefreshFlocks();
    }

    private static TextBlock RowTitle(string text) => new()
    {
        Text = text, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock RowMeta(string text, double trailing = 0) => new()
    {
        Text = text, FontSize = 12, Foreground = Palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, trailing, 0),
    };

    /// <summary>"App — title" for a window in an opened row.</summary>
    private static TextBlock WindowLine(string app, string title)
    {
        var line = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        line.Inlines =
        [
            new Avalonia.Controls.Documents.Run(app) { Foreground = Palette.Title, FontWeight = FontWeight.Medium },
            new Avalonia.Controls.Documents.Run("  " + title) { Foreground = Palette.Fg },
        ];
        return line;
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    /// <summary>Where a saved window goes back to, e.g. <c>DISPLAY1 · zones 1+2 of 3 columns</c>.</summary>
    private static string Place(SavedWindow w) => w.PlaceText() + (w.Placement.Minimised ? " (minimised)" : "");

    /// <summary>How a saved window's app would be started (as edited in the designer, else as captured).</summary>
    private static string Recipe(SavedWindow w)
    {
        var r = w.EffectiveRecipe();
        var line = r.Kind switch
        {
            LaunchKind.Skip => "skipped when loading",
            LaunchKind.AlreadyOpen => w.ExePath is null && !w.IsChanged ? "⚠ couldn't read its exe — uses an open window" : "uses a window that's already open",
            LaunchKind.StoreApp => string.IsNullOrEmpty(r.Arguments) ? $"Store app {r.Target}" : $"Store app {r.Target} {r.Arguments}",
            _ => string.IsNullOrEmpty(r.Arguments) ? r.Target : $"{r.Target} {r.Arguments}",
        };
        if (!w.IsChanged && r.Kind == LaunchKind.Program && w.Arguments is null) line += " (⚠ arguments unreadable)";
        if (w.Elevated) line = "⚠ runs as administrator · " + line;
        if (w.IsChanged) line = "edited · " + line;
        return line;
    }

    private static bool Warning(SavedWindow w) => w.Elevated || (!w.IsChanged && !w.Packaged && (w.ExePath is null || w.Arguments is null));

    private string? SlotHotkey(int slot) => Services.HotkeyService.SlotHotkeyText(S, slot);

    private static string Hotkey(string text) => Core.Hotkeys.Hotkey.TryParse(text)?.ToString() ?? "the new-flock shortcut";

    // ---- Zones ----

    private void BuildZones(StackPanel page)
    {
        var s = S;
        page.Children.Add(PageTitle("Zones"));
        page.Children.Add(BodyText(
            "Drag a window while holding the zone key to snap it into a zone. Ctrl spans several zones. Each monitor " +
            "has its own layout."));

        page.Children.Add(Row("Snap to zones", "Show the zones while dragging a window.",
            Toggle(s.ZonesEnabled, on => Update(x => x with { ZonesEnabled = on }))));

        var modifiers = new[] { "Shift", "Alt", "None" };
        var current = Array.FindIndex(modifiers, m => string.Equals(m, s.ZoneDragModifier, StringComparison.OrdinalIgnoreCase));
        page.Children.Add(Row("Zone key", "Hold this while dragging to show the zones. None shows them on every drag.",
            Segmented(["Shift", "Alt", "Every drag"], Math.Max(0, current),
                i => Update(x => x with { ZoneDragModifier = modifiers[i] })),
            s.ZonesEnabled));

        page.Children.Add(Row("Show zones on every monitor",
            "Off shows them only on the monitor you're dragging over — smoother, especially on USB-dock monitors.",
            Toggle(s.ZonesOnEveryMonitor, on => Update(x => x with { ZonesOnEveryMonitor = on })),
            s.ZonesEnabled));

        page.Children.Add(Row("Zoned windows form a flock",
            "Windows dropped into a monitor's zones minimise and restore together.",
            Toggle(s.ZonesFormFlock, on => Update(x => x with { ZonesFormFlock = on }))));

        page.Children.Add(Row("A separate zone flock on each virtual desktop",
            "Zoned windows on different desktops never minimise or restore together.",
            Toggle(s.ZoneFlockPerDesktop, on => Update(x => x with { ZoneFlockPerDesktop = on })),
            s.ZonesFormFlock));

        page.Children.Add(Row("Snap strip at the top of the screen",
            "Drag a window to the top of a monitor to drop down a strip of layouts; drop it on a zone to snap it there.",
            Toggle(s.SnapStrip, on => Update(x => x with { SnapStrip = on }))));
        if (s.SnapStrip && WindowsSnapSettings.IsOn(WindowsSnapSettings.SnapBar))
        {
            page.Children.Add(Row("Windows' snap bar is on too",
                "Both will drop down. Turn Windows' off on the Windows snapping page (then restart Explorer).",
                FlatButton("Windows snapping…", () => Select("snapping"))));
        }

        page.Children.Add(Row("Gaps between windows", "Leave space between zones and around the screen edge.",
            Toggle(s.ZoneGaps, _host.SetGaps)));

        var spacing = new NumericUpDown
        {
            Minimum = 0, Maximum = 64, Increment = 2, Value = s.ZoneSpacing, Width = 130, FormatString = "0",
        };
        spacing.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v && (int)v != S.ZoneSpacing) Update(x => x with { ZoneSpacing = (int)v });
        };
        page.Children.Add(Row("Gap size", "In pixels.", spacing, s.ZoneGaps));

        page.Children.Add(SectionTitle("Layouts"));
        foreach (var monitor in Monitors.All())
            page.Children.Add(MonitorLayouts(monitor));
    }

    // ---- Windows snapping ----

    private void BuildWindowsSnapping(StackPanel page)
    {
        page.Children.Add(PageTitle("Windows snapping"));
        page.Children.Add(BodyText(
            "Windows 11's snap layouts pop up where Flamingle's zones and layout tabs live. These are your Windows " +
            "settings (Settings → System → Multitasking), not Flamingle's. Windows applies a change made here once " +
            "Explorer restarts (or the next time you sign in)."));
        page.Children.Add(WindowsSnapRow("Snap layouts when dragging to the top of the screen",
            "The bar that drops down from the top edge while you drag a window.", WindowsSnapSettings.SnapBar));
        page.Children.Add(WindowsSnapRow("Snap layouts on the maximise button",
            "The grid that appears when you hover a window's maximise button.", WindowsSnapSettings.MaximiseFlyout));
        page.Children.Add(Row("Restart Explorer to apply",
            "The taskbar blinks out for a moment and open File Explorer windows close.",
            RestartExplorerButton()));
    }

    /// <summary>Restarting Explorer closes the user's folder windows, so it takes a second click to confirm.</summary>
    private Control RestartExplorerButton()
    {
        var armed = false;
        Button? button = null;
        button = FlatButton("Restart Explorer", async () =>
        {
            if (!armed)
            {
                armed = true;
                button!.Content = "Click again to restart";
                DispatcherTimer.RunOnce(() =>
                {
                    if (!button.IsEnabled) return;   // already restarting
                    armed = false;
                    button.Content = "Restart Explorer";
                }, TimeSpan.FromSeconds(4));
                return;
            }

            button!.IsEnabled = false;
            button.Content = "Restarting…";
            var ok = await WindowsSnapSettings.RestartExplorer();
            button.Content = ok ? "Restarted" : "Couldn't restart — sign out and in instead";
        });
        return button;
    }

    private Control MonitorLayouts(MonitorInfo monitor)
    {
        var current = _host.LayoutIdFor(monitor);
        var presets = new WrapPanel();
        foreach (var layout in ZoneLayout.Templates)
        {
            var button = new Button
            {
                Padding = new Thickness(5),
                Margin = new Thickness(0, 0, 6, 6),
                Background = layout.Id == current ? Palette.ButtonBg : Brushes.Transparent,
                BorderBrush = layout.Id == current ? Palette.AccentBrush : Palette.Line,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Content = new LayoutThumbnail(layout, 64, 40, layout.Id == current),
            };
            ToolTip.SetTip(button, layout.Name);
            var id = layout.Id;
            button.Click += (_, _) => _host.ApplyLayout(monitor, id);
            presets.Children.Add(button);
        }

        return new Border
        {
            Padding = new Thickness(2, 10),
            BorderBrush = Palette.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = monitor.Label, FontSize = 14, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title },
                    presets,
                    ButtonRow(
                        FlatButton("Show zones", () => _host.ShowZones(monitor)),
                        FlatButton("Fill with open windows", () => _host.FillZones(monitor))),
                },
            },
        };
    }

    private Control WindowsSnapRow(string name, string description, string setting)
        => Row(name, description, Toggle(WindowsSnapSettings.IsOn(setting), on => WindowsSnapSettings.Set(setting, on)));

    // ---- Taskbar ----

    private void BuildTaskbar(StackPanel page)
    {
        var s = S;
        page.Children.Add(PageTitle("Taskbar"));

        page.Children.Add(Row("A taskbar button for each flock",
            "Click it to minimise or restore the flock; close it to dissolve the flock (the windows stay open).",
            Toggle(s.TaskbarButtons, on => Update(x => x with { TaskbarButtons = on }))));

        page.Children.Add(Row("Icon",
            "The Flamingle icon numbered with the flock's monitor, or a mosaic of the flock's apps' icons.",
            Segmented(["Flamingle", "Apps"], s.TaskbarIconShowsApps ? 1 : 0,
                i => Update(x => x with { TaskbarIconStyle = i == 1 ? "Apps" : "Flamingle" })),
            s.TaskbarButtons));

        page.Children.Add(Row("Button follows the flock across virtual desktops",
            "The button moves to the desktop most of the flock's windows are on.",
            Toggle(s.TaskbarButtonFollowsDesktop, on => Update(x => x with { TaskbarButtonFollowsDesktop = on })),
            s.TaskbarButtons));

        page.Children.Add(Row("Preview shows the flock's windows",
            "Hovering the button shows the windows laid out as they are on screen, with Minimise / restore and " +
            "Dissolve buttons under the preview.",
            Toggle(s.TaskbarPreviews, on => Update(x => x with { TaskbarPreviews = on })),
            s.TaskbarButtons));

        page.Children.Add(Row("Hovering the button outlines the flock's windows",
            "Draws an outline around each of the flock's windows (dashed for minimised ones) while you hover.",
            Toggle(s.TaskbarHoverOutline, on => Update(x => x with { TaskbarHoverOutline = on })),
            s.TaskbarButtons && s.TaskbarPreviews));
    }

    // ---- Behaviour ----

    private void BuildBehaviour(StackPanel page)
    {
        var s = S;
        page.Children.Add(PageTitle("Behaviour"));
        page.Children.Add(BodyText("All of these are experiments — switch them off to compare."));

        page.Children.Add(Row("Linked minimise",
            "Minimising or restoring any one window in a flock (its own title bar, the taskbar) takes the rest with it.",
            Toggle(s.LinkedMinimise, on => Update(x => x with { LinkedMinimise = on }))));

        page.Children.Add(Row("Put windows back exactly after a restore",
            "For apps that restore their own size and position instead of where they were.",
            Toggle(s.CorrectPositionsOnRestore, on => Update(x => x with { CorrectPositionsOnRestore = on }))));

        page.Children.Add(Row("Restore a window's size as soon as it's pulled out of a layout",
            "Rather than when it's dropped. Hold the zone key before it shrinks to keep its zone size.",
            Toggle(s.RestoreSizeDuringDrag, on => Update(x => x with { RestoreSizeDuringDrag = on }))));

        page.Children.Add(Row("Resizing a zoned window moves its neighbours",
            "Dragging an inner edge moves that split, like Windows' Snap Groups.",
            Toggle(s.LinkedResize, on => Update(x => x with { LinkedResize = on }))));

        page.Children.Add(Row("Design saved flocks on screen",
            "Editing a saved flock draws its layout over your screens: click a zone to choose what starts there, with " +
            "a floating panel for whatever you select. Off: the form here in Settings.",
            Toggle(s.DesignModeEditing, on => Update(x => x with { DesignModeEditing = on }))));
    }

    // ---- Shortcuts ----

    private void BuildShortcuts(StackPanel page)
    {
        var s = S;
        page.Children.Add(PageTitle("Shortcuts"));
        page.Children.Add(BodyText("Global shortcuts. Click one, then press the new combination (Esc cancels)."));

        page.Children.Add(Row("New flock", "Opens the picker, with the windows snapped next to the focused one ticked.",
            ShortcutEditor(s.NewFlockHotkey, v => Update(x => x with { NewFlockHotkey = v }))));
        page.Children.Add(Row("Minimise / restore", "The flock holding the focused window, else the last one used.",
            ShortcutEditor(s.ToggleHotkey, v => Update(x => x with { ToggleHotkey = v }))));
        page.Children.Add(Row("Edit layout",
            "Puts the layout on the monitor under the cursor into edit mode — even with no windows in it yet: drag " +
            "anywhere to move it, adjust its splits, pick a layout. Ctrl+Z / Ctrl+Y undo and redo layout changes. " +
            "Press again (or Esc) to finish.",
            ShortcutEditor(s.LayoutMenuHotkey, v => Update(x => x with { LayoutMenuHotkey = v }))));

        page.Children.Add(Row("Flock slots", "These keys plus 1–9 minimise / restore flock 1–9. None turns them off.",
            SlotModifierPicker(s.SlotHotkeyModifiers)));
    }

    private Control ShortcutEditor(string current, Action<string> set)
    {
        var capture = new HotkeyCaptureButton(current, _host.SuspendHotkeys);
        capture.Captured += set;
        var clear = new Button
        {
            Content = "✕", Padding = new Thickness(8, 4), Background = Brushes.Transparent, Foreground = Palette.Muted,
            BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(clear, "Turn this shortcut off");
        clear.Click += (_, _) => set("");
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { capture, clear } };
    }

    private Control SlotModifierPicker(string current)
    {
        var parsed = Core.Hotkeys.Hotkey.TryParse($"{current}+1")?.Modifiers ?? Core.Hotkeys.HotkeyModifiers.None;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var (flag, label) in new[]
                 {
                     (Core.Hotkeys.HotkeyModifiers.Control, "Ctrl"), (Core.Hotkeys.HotkeyModifiers.Alt, "Alt"),
                     (Core.Hotkeys.HotkeyModifiers.Shift, "Shift"), (Core.Hotkeys.HotkeyModifiers.Win, "Win"),
                 })
        {
            var on = parsed.HasFlag(flag);
            var chip = new ToggleButton
            {
                Content = label, IsChecked = on, FontSize = 12, Padding = new Thickness(10, 4),
                CornerRadius = new CornerRadius(12),
            };
            chip.IsCheckedChanged += (_, _) =>
            {
                var mods = chip.IsChecked == true ? parsed | flag : parsed & ~flag;
                Update(x => x with { SlotHotkeyModifiers = ModifierText(mods) });
            };
            panel.Children.Add(chip);
        }
        return panel;
    }

    private static string ModifierText(Core.Hotkeys.HotkeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(Core.Hotkeys.HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(Core.Hotkeys.HotkeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Core.Hotkeys.HotkeyModifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Core.Hotkeys.HotkeyModifiers.Win)) parts.Add("Win");
        return string.Join("+", parts);
    }

    // ---- About ----

    private void BuildAbout(StackPanel page)
    {
        page.Children.Add(PageTitle($"Flamingle{AppProfile.DisplaySuffix}"));
        page.Children.Add(BodyText($"Version {_host.Version}"));
        page.Children.Add(BodyText("Group windows so they minimise, restore, move and relaunch together."));

        page.Children.Add(SectionTitle("Data"));
        page.Children.Add(BodyText(
            $"Settings, flocks and layouts are kept in {AppProfile.DataDirectory()}. settings.json can be edited by " +
            "hand too; changes made here are written straight to it."));
        page.Children.Add(ButtonRow(FlatButton("Open data folder", _host.OpenDataFolder)));
    }

    // ---- Changelog ----

    private void BuildChangelog(StackPanel page)
    {
        page.Children.Add(PageTitle("Changelog"));
        page.Children.Add(Row("Show what's new after an update",
            "On the first launch after Flamingle updates, list the releases since the version you were on.",
            Toggle(S.ShowChangelogOnUpdate, on => Update(x => x with { ShowChangelogOnUpdate = on }))));

        if (AppProfile.IsDev)
        {
            var from = new TextBox { Text = "v0.0.0", Width = 96, FontSize = 13 };
            var to = new TextBox { Text = "v" + _host.Version, Width = 96, FontSize = 13 };
            var range = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            range.Children.Add(from);
            range.Children.Add(new TextBlock { Text = "→", Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center });
            range.Children.Add(to);
            range.Children.Add(FlatButton("Preview window", () => _host.PreviewChangelog(from.Text ?? "", to.Text ?? "")));
            page.Children.Add(Row("Preview (dev builds)",
                "The window an update between these versions would show — after the first, up to and including the second.",
                range));
        }

        if (ChangelogMarkdown.LoadEmbedded() is not { } markdown)
        {
            page.Children.Add(BodyText("Changelog not available."));
            return;
        }
        ChangelogMarkdown.Render(page, markdown.Split('\n'));
    }
}
