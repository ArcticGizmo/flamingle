using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Flamingle.App.Native;
using Flamingle.Core.Saved;
using static Flamingle.App.Views.Settings.SettingsUi;

namespace Flamingle.App.Views.Settings;

/// <summary>The parts of the editor only design mode's floating panel shows: a selected window, the "what starts
/// here?" chooser for an empty zone, and the hint shown when nothing is selected.</summary>
internal sealed partial class SavedFlockEditor
{
    private static readonly string[] AssignTabs = ["Open windows", "Installed apps"];

    // The chooser's tab and search, kept while it's open.
    private int _assignTab;
    private string _assignSearch = "";

    private Control DesignFlockHint()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Muted(_draft.Windows.Count == 0
            ? "Click a zone on screen to choose what starts there."
            : "Click a zone to choose what starts there, or a window to change how it starts. Drag a window to another "
              + "zone to move it; hold Ctrl as you drop to span the zones in between."));
        if (_design is { } design && !Dirty) body.Children.Add(Link("Edit as a form in Settings instead", design.FormEditor));
        return body;
    }

    // ---- A selected window ----

    private Control DesignWindow(int index)
    {
        var window = _draft.Windows[index];
        var body = new StackPanel { Spacing = 12 };

        var title = new StackPanel { Spacing = 1, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock
        {
            Text = window.ProcessName, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        title.Children.Add(new TextBlock
        {
            Text = window.Title.Length > 0 ? window.Title : window.EffectiveRecipe().Target, FontSize = 12, Foreground = Palette.Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var change = Link("Change app", () => Show(new EditorFocus.Assign($"Instead of {window.ProcessName}", index)));
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(Tile(window.ProcessName.Length > 0 ? window.ProcessName[..1].ToUpperInvariant() : "?", index));
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        Grid.SetColumn(change, 2);
        head.Children.Add(change);
        body.Children.Add(head);

        if (_design is { } design)
        {
            body.Children.Add(Heading("Goes"));
            body.Children.Add(design.Placement(index));
        }
        body.Children.Add(Heading("Starts"));
        body.Children.Add(WindowDetails(index, new Thickness(0)));
        return body;
    }

    // ---- What starts here? ----

    private Control AssignChooser(EditorFocus.Assign focus)
    {
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(Heading(focus.Replacing is null ? "What starts here?" : "Start something else here"));
        body.Children.Add(Muted(focus.Where));
        body.Children.Add(Segmented(AssignTabs, _assignTab, i =>
        {
            _assignTab = i;
            Build();
        }));

        var search = new TextBox { Text = _assignSearch, PlaceholderText = "Search", FontSize = 13 };
        var list = new StackPanel { Spacing = 1 };
        var footer = new StackPanel { Spacing = 4 };
        body.Children.Add(search);
        body.Children.Add(new ScrollViewer { MaxHeight = 320, Content = list });
        body.Children.Add(footer);

        void Assign(SavedWindow? w)
        {
            if (w is not null) _design?.Assign(w, focus.Replacing);
        }

        void Fill()
        {
            list.Children.Clear();
            var q = _assignSearch.Trim();
            bool Hit(params string?[] fields) => q.Length == 0 || fields.Any(f => f?.Contains(q, StringComparison.OrdinalIgnoreCase) == true);

            if (_assignTab == 0)
            {
                foreach (var choice in _openWindows().Where(c => Hit(c.App, c.Title)))
                    list.Children.Add(ChoiceRow(choice.App, choice.Title, () => Assign(_capture(choice.Handle, 0))));
                if (list.Children.Count == 0) list.Children.Add(Muted(q.Length > 0 ? "No open window matches." : "No windows are open."));
                return;
            }

            var apps = InstalledApps.All();
            if (!apps.IsCompleted)
            {
                list.Children.Add(Muted("Reading the Start menu…"));
                apps.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                {
                    if (TopLevel.GetTopLevel(list) is not null) Fill();   // still showing
                }));
                return;
            }
            foreach (var app in apps.Result.Where(a => Hit(a.Name, a.ExePath, a.AppUserModelId)).Take(200))
            {
                var detail = app.ExePath is { } exe ? Path.GetFileName(exe) : "Store app";
                list.Children.Add(ChoiceRow(app.Name, detail, () => Assign(app.ExePath is { } path
                    ? FlockDesign.ForProgram(path, app.Arguments)
                    : FlockDesign.ForStoreApp(app.AppUserModelId!, app.Name))));
            }
            if (list.Children.Count == 0) list.Children.Add(Muted(q.Length > 0 ? "No app matches." : "No apps found."));
        }

        search.TextChanged += (_, _) =>
        {
            _assignSearch = search.Text ?? "";
            Fill();
        };
        Fill();

        if (_assignTab == 0)
        {
            if (_design is { } design) footer.Children.Add(Link("Or drag a window onto a zone", design.DragIn));
        }
        else
        {
            footer.Children.Add(Link("Not listed? Browse for a program…", () => _ = Browse(Assign)));
        }
        footer.Children.Add(Link("Cancel", () =>
        {
            MoveFocus(focus.Replacing is { } i ? new EditorFocus.Window(i) : new EditorFocus.Flock());
            Build();
        }, Palette.Muted));
        return body;
    }

    private async Task Browse(Action<SavedWindow?> assign)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a program",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Programs") { Patterns = ["*.exe"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) assign(FlockDesign.ForProgram(path));
    }

    private static Button ChoiceRow(string name, string detail, Action pick)
    {
        var text = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines = new InlineCollection
        {
            new Run(name) { Foreground = Palette.Title, FontWeight = FontWeight.Medium },
            new Run("  " + detail) { Foreground = Palette.Muted, FontSize = 12 },
        };
        var button = new Button
        {
            Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 6), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, Foreground = Palette.Fg,
        };
        button.Click += (_, _) => pick();
        return button;
    }
}
