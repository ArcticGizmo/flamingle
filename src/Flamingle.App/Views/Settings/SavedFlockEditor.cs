using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Flamingle.Core.Persistence;
using Flamingle.Core.Saved;
using static Flamingle.App.Views.Settings.SettingsUi;

namespace Flamingle.App.Views.Settings;

/// <summary>An open window offered by the designer's "Add a window".</summary>
internal sealed record WindowChoice(long Handle, string App, string Title);

/// <summary>What the editor shows: the whole form (Settings › Flocks › Edit), or one part of it — in design mode's
/// floating panel, which follows what's selected on screen.</summary>
internal abstract record EditorFocus
{
    public sealed record Form : EditorFocus;

    /// <summary>Nothing selected: the flock itself — its inputs and Test load.</summary>
    public sealed record Flock : EditorFocus;

    /// <summary>A window: what starts there, and where it goes.</summary>
    public sealed record Window(int Index) : EditorFocus;

    /// <summary>Choose what starts in an empty region (<paramref name="Where"/>), or instead of window
    /// <paramref name="Replacing"/>.</summary>
    public sealed record Assign(string Where, int? Replacing = null) : EditorFocus;

    /// <summary>Something design mode builds itself (e.g. a monitor's layout picker), under the panel's header.</summary>
    public sealed record Custom(string Title, Func<Control> Build) : EditorFocus;
}

/// <summary>What design mode does with the panel's choices.</summary>
/// <param name="Placement">Builds "where it goes" for a window.</param>
/// <param name="Assign">Something was chosen for an <see cref="EditorFocus.Assign"/> focus (the window, and which
/// window it replaces, if any).</param>
/// <param name="DragIn">"Drag a window onto a zone" — the screens let the next drag through.</param>
/// <param name="FormEditor">Leave design mode for the form in Settings (only offered without unsaved changes).</param>
internal sealed record DesignHooks(
    Func<int, Control> Placement,
    Action<SavedWindow, int?> Assign,
    Action DragIn,
    Action FormEditor);

/// <summary>
/// The saved-flock designer (Settings › Flocks › Edit): what a saved flock asks for when it loads and how each of its
/// windows is opened — laid out to disclose progressively, as in the template designer canvas
/// (<c>docs/artefacts/template-designer</c>). Works on a copy until Save changes.
///
/// <para>Structural changes (opening a window, adding an input, switching a kind) rebuild the page; typing only
/// updates the draft and refreshes what's derived from it (summaries, "Becomes" lines, problems, Test load), so the
/// text box being typed in keeps its focus.</para>
/// </summary>
internal sealed partial class SavedFlockEditor : Border
{
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    private static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#FDBA74"));
    private static readonly IBrush WarnBg = new SolidColorBrush(Color.Parse("#3B2A17"));
    private static readonly IBrush SuggestBg = new SolidColorBrush(Color.Parse("#12303D"));
    private static readonly IBrush ValueBg = new SolidColorBrush(Color.Parse("#267DD3FC"));
    private static readonly IBrush Code = new SolidColorBrush(Color.Parse("#C9D3DC"));
    private static readonly string[] Tints = ["#7DD3FC", "#A7F3D0", "#FDBA74", "#C4B5FD", "#F9A8D4", "#FDE68A"];

    private static readonly LaunchKind[] KindOrder = [LaunchKind.Program, LaunchKind.StoreApp, LaunchKind.AlreadyOpen, LaunchKind.Skip];
    private static readonly string[] KindLabels = ["Program", "Store app", "Already open", "Skip"];
    private static readonly IfAlreadyOpen[] IfOpenOrder = [IfAlreadyOpen.UseIt, IfAlreadyOpen.StartAnother, IfAlreadyOpen.Ask];
    private static readonly string[] IfOpenLabels = ["Use it", "Start another", "Ask"];
    private static readonly InputKind[] InputKinds = Enum.GetValues<InputKind>();

    private static readonly Dictionary<InputKind, string> KindHints = new()
    {
        [InputKind.Text] = "Typed in when the flock loads.",
        [InputKind.Folder] = "Picked with a folder browser.",
        [InputKind.File] = "Picked with a file browser.",
        [InputKind.Number] = "A whole number — a port, a display.",
        [InputKind.Choice] = "One of these, comma-separated. The first is the default.",
        [InputKind.Computed] = "Not asked for: this command runs when the flock loads, and the first line it prints becomes the value.",
    };

    private readonly Action<SavedFlock> _save;
    private readonly Action _back;
    private readonly Func<IReadOnlyList<WindowChoice>> _openWindows;
    private readonly Func<long, int, SavedWindow?> _capture;
    private SavedFlock _saved;
    private SavedFlock _draft;
    private bool _isNew;
    private bool _picking;

    // What's opened up.
    private int? _openWindow;
    private readonly HashSet<int> _more = [];
    private readonly HashSet<int> _startIn = [];
    private string? _openInput;
    private bool _testing;
    private bool _showStored;
    private bool _confirmDiscard;
    private readonly Dictionary<string, string> _testValues = new(StringComparer.OrdinalIgnoreCase);

    // Refreshed after every edit, without rebuilding.
    private readonly List<Action> _derived = [];
    private IReadOnlyList<FlockProblem> _problems = [];
    private Dictionary<string, string> _values = [];

    // Design mode: what's in focus, and what its choices do (null in Settings' form).
    private EditorFocus _focus = new EditorFocus.Form();
    private readonly DesignHooks? _design;

    /// <param name="isNew">A flock started from scratch: not stored until Save changes.</param>
    /// <param name="openWindows">The windows open now, to add one from.</param>
    /// <param name="capture">Capture an open window (handle, Z index) as a saved window, as saving a flock does.</param>
    /// <param name="design">Set in design mode's panel: the editor then shows one part at a time (see
    /// <see cref="Show"/>), and Done replaces the back link.</param>
    public SavedFlockEditor(SavedFlock flock, bool isNew, Action<SavedFlock> save, Action back,
        Func<IReadOnlyList<WindowChoice>> openWindows, Func<long, int, SavedWindow?> capture, DesignHooks? design = null)
    {
        _saved = _draft = flock;
        _isNew = isNew;
        _save = save;
        _back = back;
        _openWindows = openWindows;
        _capture = capture;
        _design = design;
        if (design is not null) _focus = new EditorFocus.Flock();
        _picking = isNew && design is null;   // a new flock starts by choosing its windows (on screen, by clicking a zone)
        Build();
    }

    /// <summary>The flock as edited so far.</summary>
    public SavedFlock Draft => _draft;

    /// <summary>The draft changed (as typed, or restructured).</summary>
    public event Action<SavedFlock>? DraftChanged;

    /// <summary>The editor moved its own focus (e.g. back to the flock after removing a window, or for Test load).</summary>
    public event Action<EditorFocus>? FocusChanged;

    /// <summary>Show one part (design mode).</summary>
    public void Show(EditorFocus focus)
    {
        _focus = focus;
        if (focus is EditorFocus.Window or EditorFocus.Assign) _testing = false;
        Build();
    }

    /// <summary>A change made outside the editor (design mode moving a window, picking an app).</summary>
    public void Apply(SavedFlock draft) => Restructure(draft);

    private void MoveFocus(EditorFocus focus)
    {
        _focus = focus;
        FocusChanged?.Invoke(focus);
    }

    // A new flock has something to save once it has a window or an input; until then leaving it loses nothing.
    private bool Dirty => _isNew
        ? _draft.Windows.Count > 0 || _draft.InputList.Count > 0
        : JsonFile<SavedFlock>.ToJson(_draft) != JsonFile<SavedFlock>.ToJson(_saved);

    /// <summary>Esc / the back link: leave, asking first if there are unsaved changes.</summary>
    public void Back()
    {
        if (Dirty && !_confirmDiscard)
        {
            _confirmDiscard = true;
            Build();
            return;
        }
        _back();
    }

    // ---- Page ----

    private void Build()
    {
        _derived.Clear();
        var page = new StackPanel { Spacing = _design is null ? 22 : 16 };
        page.Children.Add(Header());
        page.Children.Add(ProblemList());
        switch (_focus)
        {
            case EditorFocus.Window w when w.Index < _draft.Windows.Count:
                page.Children.Add(DesignWindow(w.Index));
                break;
            case EditorFocus.Assign a:
                page.Children.Add(AssignChooser(a));
                break;
            case EditorFocus.Custom c:
                page.Children.Add(Heading(c.Title));
                page.Children.Add(c.Build());
                break;
            case EditorFocus.Form:
                if (_testing) page.Children.Add(TestLoad());
                page.Children.Add(AsksFor());
                page.Children.Add(Starts());
                break;
            default:   // the flock (or a window that's gone)
                if (_testing) page.Children.Add(TestLoad());
                page.Children.Add(AsksFor());
                page.Children.Add(DesignFlockHint());
                break;
        }
        Child = page;
        Refresh();
    }

    private void Refresh()
    {
        _problems = FlockProblems.Find(_draft);
        _values = PreviewValues();
        foreach (var refresh in _derived) refresh();
    }

    /// <summary>The values Test load would use: what's typed there, else each input's default; computed inputs show
    /// as ⟨name⟩ (they're only worked out at load).</summary>
    private Dictionary<string, string> PreviewValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in _draft.InputList)
        {
            var value = input.Kind == InputKind.Computed ? $"⟨{input.Name}⟩"
                : _testValues.TryGetValue(input.Name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim()
                : input.DefaultValue();
            values.TryAdd(input.Name, value);
        }
        return values;
    }

    private void Derive(Action refresh) => _derived.Add(refresh);

    private void Edit(SavedFlock draft)
    {
        _draft = draft;
        _confirmDiscard = false;
        Refresh();
        DraftChanged?.Invoke(draft);
    }

    private void Restructure(SavedFlock draft)
    {
        _draft = draft;
        _confirmDiscard = false;
        Build();
        DraftChanged?.Invoke(draft);
    }

    // ---- Header ----

    private Control Header()
    {
        var panel = new StackPanel { Spacing = 4 };
        if (_design is null) panel.Children.Add(Link("‹ Flocks", Back));

        var name = new TextBox
        {
            Text = _draft.Name, FontSize = _design is null ? 20 : 17, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2),
            MinWidth = 200, HorizontalAlignment = HorizontalAlignment.Left,
        };
        name.TextChanged += (_, _) => Edit(_draft with { Name = name.Text ?? "" });

        var subtitle = new TextBlock { FontSize = 13, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap };
        Derive(() =>
        {
            var n = _draft.Windows.Count;
            var asks = _draft.InputList.Count;
            subtitle.Text = $"{n} window{(n == 1 ? "" : "s")} · saved {_draft.SavedAt.LocalDateTime:d MMM, HH:mm}"
                            + (asks > 0 ? $" · asks for {asks} value{(asks == 1 ? "" : "s")}" : "")
                            + (Dirty ? " · unsaved changes" : "");
        });

        var test = FlatButton("Test load", () =>
        {
            _testing = !_testing;
            // In design mode Test load belongs to the flock, not to whatever's selected.
            if (_testing && _design is not null && _focus is not EditorFocus.Flock) MoveFocus(new EditorFocus.Flock());
            Build();
        });
        if (_testing)
        {
            test.BorderBrush = Palette.AccentBrush;
            test.BorderThickness = new Thickness(1);
            test.Foreground = Palette.AccentBrush;
        }
        var save = FlatButton("Save changes", () =>
        {
            _saved = _draft;
            _isNew = false;
            _confirmDiscard = false;
            _save(_draft);
            Build();
        });
        save.Background = Palette.AccentBrush;
        save.Foreground = Palette.OnAccent;
        Derive(() => save.IsEnabled = Dirty && !string.IsNullOrWhiteSpace(_draft.Name));

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        top.Children.Add(new StackPanel { Children = { name, subtitle } });
        var buttons = _design is null ? ButtonRow(test, save) : ButtonRow(test, save, FlatButton("Done", Back));
        buttons.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(buttons, 1);
        if (_design is not null)
        {
            // The panel is narrow: name above, buttons beneath.
            top.ColumnDefinitions = new ColumnDefinitions("*");
            Grid.SetColumn(buttons, 0);
            top.RowDefinitions = new RowDefinitions("Auto,Auto");
            Grid.SetRow(buttons, 1);
        }
        top.Children.Add(buttons);
        panel.Children.Add(top);

        if (_confirmDiscard)
        {
            var confirm = new WrapPanel();
            confirm.Children.Add(new TextBlock
            {
                Text = "Leave without saving your changes?", Foreground = Warn, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            });
            confirm.Children.Add(FlatButton("Discard changes", _back));
            confirm.Children.Add(new Border { Width = 8 });
            confirm.Children.Add(FlatButton("Keep editing", () =>
            {
                _confirmDiscard = false;
                Build();
            }));
            panel.Children.Add(confirm);
        }
        return panel;
    }

    private Control ProblemList()
    {
        var list = new StackPanel { Spacing = 4 };
        var box = new Border
        {
            Background = WarnBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 10), Child = list,
        };
        Derive(() =>
        {
            box.IsVisible = _problems.Count > 0;
            list.Children.Clear();
            foreach (var p in _problems)
                list.Children.Add(new TextBlock { Text = p.Message, Foreground = Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        });
        return box;
    }

    // ---- Asks for ----

    private Control AsksFor()
    {
        var section = new StackPanel { Spacing = 10 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(Heading("Asks for"));
        if (_draft.InputList.Count > 0)
        {
            var add = Link("Add input", AddInput);
            Grid.SetColumn(add, 1);
            title.Children.Add(add);
        }
        section.Children.Add(title);

        if (_draft.InputList.Count == 0)
        {
            var empty = new WrapPanel();
            empty.Children.Add(new TextBlock
            {
                Text = "Nothing — it starts the same way every time.", Foreground = Palette.Muted, FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0),
            });
            empty.Children.Add(Link("Add an input", AddInput));
            section.Children.Add(empty);
        }

        if (InputSuggester.Suggest(_draft, _draft.DismissedList) is { } suggestion)
            section.Children.Add(SuggestionCard(suggestion));

        if (_draft.InputList.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var input in _draft.InputList) chips.Children.Add(InputChip(input));
            section.Children.Add(chips);
        }

        if (_openInput is not null && _draft.Input(_openInput) is { } open)
            section.Children.Add(InputEditor(open));
        return section;
    }

    private Control SuggestionCard(InputSuggestion s)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Palette.Fg, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines = new InlineCollection
        {
            new Run(s.Path) { FontFamily = Mono, Foreground = Palette.AccentBrush },
            new Run($" is used by {(s.Windows == 1 ? "one window" : $"{s.Windows} windows")}. Choose it each time you load instead?"),
        };
        var make = FlatButton("Make it an input", () =>
        {
            var made = InputSuggester.MakeInput(_draft, s.Path);
            _openInput = made.InputList[^1].Name;
            Restructure(made);
        });
        make.Background = Palette.AccentBrush;
        make.Foreground = Palette.OnAccent;
        var no = FlatButton("No thanks", () => Restructure(_draft with { Dismissed = [.. _draft.DismissedList, s.Path] }));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(text);
        var buttons = ButtonRow(make, no);
        buttons.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return new Border { Background = SuggestBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12), Child = grid };
    }

    private Control InputChip(FlockInput input)
    {
        var open = string.Equals(_openInput, input.Name, StringComparison.OrdinalIgnoreCase);
        var label = new TextBlock { Text = input.Label, FontSize = 13, FontWeight = FontWeight.Medium };
        var kind = new TextBlock { Text = input.Kind.ToString().ToLowerInvariant(), FontSize = 12, Foreground = Palette.Muted, Margin = new Thickness(8, 0, 0, 0) };
        var button = new Button
        {
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, kind } },
            Background = open ? SuggestBg : Palette.Row,
            BorderBrush = open ? Palette.AccentBrush : Palette.Line,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(12, 6),
            Margin = new Thickness(0, 0, 8, 8), Foreground = Palette.Fg,
        };
        button.Click += (_, _) =>
        {
            _openInput = open ? null : input.Name;
            Build();
        };
        Derive(() => label.Text = _draft.Input(input.Name)?.Label ?? input.Label);
        return button;
    }

    private Control InputEditor(FlockInput input)
    {
        var name = input.Name;
        FlockInput Current() => _draft.Input(name) ?? input;
        void Set(Func<FlockInput, FlockInput> change) => Edit(_draft with
        {
            Inputs = _draft.InputList.Select(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase) ? change(i) : i).ToList(),
        });

        var body = new StackPanel { Spacing = 12 };

        var label = Field(input.Label, false, t => Set(i => i with { Label = t }));
        var kind = new ComboBox { ItemsSource = InputKinds.Select(k => k.ToString()).ToList(), SelectedIndex = Array.IndexOf(InputKinds, input.Kind), MinWidth = 140 };
        kind.SelectionChanged += (_, _) =>
        {
            if (kind.SelectedIndex < 0 || InputKinds[kind.SelectedIndex] == Current().Kind) return;
            var k = InputKinds[kind.SelectedIndex];
            Restructure(_draft with
            {
                Inputs = _draft.InputList.Select(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase) ? i with { Kind = k } : i).ToList(),
            });
        };
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,12,Auto") };
        top.Children.Add(Labelled("Asked as", label));
        var kindBox = Labelled("Kind", kind);
        Grid.SetColumn(kindBox, 2);
        top.Children.Add(kindBox);
        body.Children.Add(top);

        var defLabel = input.Kind switch { InputKind.Computed => "Command", InputKind.Choice => "Choices", _ => "Default" };
        var def = Field(input.Default, true, t => Set(i => i with { Default = t }));
        var defBox = Labelled(defLabel, def);
        defBox.Children.Add(Hint(KindHints[input.Kind]));
        body.Children.Add(defBox);

        if (input.Kind is not (InputKind.Computed or InputKind.Choice))
        {
            var remember = new CheckBox { Content = "Offer recently used values first", IsChecked = input.RememberRecent, FontSize = 13 };
            remember.IsCheckedChanged += (_, _) => Set(i => i with { RememberRecent = remember.IsChecked == true });
            body.Children.Add(remember);
        }

        // The name used in commands: renamed (everywhere it's used) when the box is left or Enter is pressed.
        var nameBox = new TextBox { Text = input.Name, FontFamily = Mono, FontSize = 12, Width = 140, Padding = new Thickness(6, 3) };
        var nameError = new TextBlock { Foreground = Warn, FontSize = 12, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        var committed = false;   // the rebuild after a rename takes this box away, which fires LostFocus again
        void CommitName()
        {
            var to = nameBox.Text?.Trim() ?? "";
            if (committed || to == name) return;
            var taken = _draft.InputList.Any(i => !string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)
                                                  && string.Equals(i.Name, to, StringComparison.OrdinalIgnoreCase));
            if (!FlockInput.IsValidName(to) || taken)
            {
                nameError.Text = taken ? $"Another input is already called {to}." : "Letters, digits and _ only, not starting with a digit.";
                nameError.IsVisible = true;
                return;
            }
            committed = true;
            _openInput = to;
            Restructure(_draft.RenameInput(name, to));
        }
        nameBox.LostFocus += (_, _) => CommitName();
        nameBox.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitName();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);

        var usage = new TextBlock { FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };
        Derive(() =>
        {
            var n = _draft.Windows.Count(w => w.EffectiveRecipe().Templates().Any(t => Placeholders.Uses(t, name)));
            usage.Text = n switch { 0 => "· not used yet", 1 => "· used by 1 window", _ => $"· used by {n} windows" };
        });

        var naming = new WrapPanel();
        naming.Children.Add(Muted("Written in commands as ", center: true));
        naming.Children.Add(new TextBlock { Text = "${", FontFamily = Mono, Foreground = Palette.AccentBrush, VerticalAlignment = VerticalAlignment.Center });
        naming.Children.Add(nameBox);
        naming.Children.Add(new TextBlock { Text = "}", FontFamily = Mono, Foreground = Palette.AccentBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        naming.Children.Add(usage);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        footer.Children.Add(new StackPanel { Spacing = 4, Children = { naming, nameError } });
        var remove = Link("Remove input", () =>
        {
            _openInput = null;
            Restructure(_draft with { Inputs = _draft.InputList.Where(i => !string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)).ToList() });
        }, Warn);
        Grid.SetColumn(remove, 1);
        footer.Children.Add(remove);
        body.Children.Add(new Border
        {
            BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0), Child = footer,
        });

        return Card(body);
    }

    private void AddInput()
    {
        var name = InputSuggester.FreeName(_draft, "input");
        _openInput = name;
        Restructure(_draft with { Inputs = [.. _draft.InputList, new FlockInput(name, "New input", InputKind.Text)] });
    }

    // ---- Starts ----

    private Control Starts()
    {
        var section = new StackPanel { Spacing = 10 };
        var title = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        title.Children.Add(Heading("Starts"));
        if (!_picking)
        {
            var add = Link("Add a window", () =>
            {
                _picking = true;
                Build();
            });
            Grid.SetColumn(add, 1);
            title.Children.Add(add);
        }
        section.Children.Add(title);

        if (_picking) section.Children.Add(WindowPicker());
        if (_draft.Windows.Count == 0)
        {
            if (!_picking) section.Children.Add(Muted("Nothing yet — add the windows it should open."));
            return section;
        }

        var rows = new StackPanel();
        for (var i = 0; i < _draft.Windows.Count; i++)
        {
            if (i > 0) rows.Children.Add(new Border { Height = 1, Background = Palette.Line });
            rows.Children.Add(WindowRow(i));
        }
        section.Children.Add(new Border { Background = Palette.Row, CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = rows });
        return section;
    }

    /// <summary>Choose an open window to add: it's captured as it is — its app, how it was started, where it sits.</summary>
    private Control WindowPicker()
    {
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(Heading("Add a window"));
        body.Children.Add(Muted("Pick one that's open now. It's added as it is — its app, how it was started and where it sits — and you can change how it starts afterwards."));

        var list = new StackPanel { Spacing = 2 };
        foreach (var choice in _openWindows())
        {
            var line = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            line.Inlines = new InlineCollection
            {
                new Run(choice.App) { Foreground = Palette.Title, FontWeight = FontWeight.Medium },
                new Run("  " + choice.Title) { Foreground = Palette.Fg },
            };
            var add = FlatButton("Add", () => AddWindow(choice.Handle));
            add.Padding = new Thickness(10, 3);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            row.Children.Add(line);
            Grid.SetColumn(add, 1);
            row.Children.Add(add);
            list.Children.Add(row);
        }
        if (list.Children.Count == 0) list.Children.Add(Muted("No windows are open."));
        body.Children.Add(new ScrollViewer { MaxHeight = 280, Content = list });

        if (_draft.Windows.Count > 0 || !_isNew)
        {
            body.Children.Add(Link("Done adding", () =>
            {
                _picking = false;
                Build();
            }));
        }
        return Card(body);
    }

    private void AddWindow(long handle)
    {
        var z = _draft.Windows.Count == 0 ? 0 : _draft.Windows.Max(w => w.ZIndex) + 1;
        if (_capture(handle, z) is not { } window) return;
        _openWindow = _draft.Windows.Count;
        Restructure(_draft with { Windows = [.. _draft.Windows, window] });
    }

    private void RemoveWindow(int index)
    {
        _openWindow = null;
        _more.Clear();
        _startIn.Clear();
        if (_design is not null) MoveFocus(new EditorFocus.Flock());
        Restructure(_draft with { Windows = _draft.Windows.Where((_, i) => i != index).ToList() });
    }

    private Control WindowRow(int index)
    {
        var window = _draft.Windows[index];
        var recipe = window.EffectiveRecipe();
        var open = _openWindow == index;

        var badge = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.Parse(Tints[index % Tints.Length])),
            Child = new TextBlock
            {
                Text = window.ProcessName.Length > 0 ? window.ProcessName[..1].ToUpperInvariant() : "?",
                Foreground = Palette.OnAccent, FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var line = new WrapPanel();
        line.Children.Add(new TextBlock { Text = window.ProcessName, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title, Margin = new Thickness(0, 0, 8, 0) });
        line.Children.Add(new TextBlock { Text = window.PlaceText(), FontSize = 12, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        var note = recipe.Kind switch
        {
            LaunchKind.AlreadyOpen => "already open",
            LaunchKind.Skip => "skipped",
            _ => recipe.IfOpen == IfAlreadyOpen.StartAnother ? "always new" : recipe.IfOpen == IfAlreadyOpen.Ask ? "asks first" : null,
        };
        if (note is not null) line.Children.Add(Pill(note, Palette.ButtonBg, Code));
        var problem = Pill("Unknown input", WarnBg, Warn);
        Derive(() => problem.IsVisible = _problems.Any(p => p.Window == index));
        line.Children.Add(problem);

        var summary = new TextBlock { FontFamily = Mono, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };
        Derive(() =>
        {
            var r = _draft.Windows[index].EffectiveRecipe();
            if (r.Kind == LaunchKind.Skip) Plain(summary, "Left out when loading");
            else if (!r.Launches) Plain(summary, "Never started — uses a window that's already open");
            else if (r.Arguments.Length == 0) Plain(summary, "no arguments");
            else Show(summary, Placeholders.Expand(r.Arguments, _values));
        });

        var chevron = new TextBlock
        {
            Text = open ? "" : "", FontFamily = Icons, FontSize = 12, Foreground = Palette.Muted,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        head.Children.Add(badge);
        var middle = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0), Children = { line, summary } };
        Grid.SetColumn(middle, 1);
        head.Children.Add(middle);
        Grid.SetColumn(chevron, 2);
        head.Children.Add(chevron);

        var toggle = new Button
        {
            Content = head, Background = Brushes.Transparent, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(0),
            Padding = new Thickness(14, 12), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Foreground = Palette.Fg,
        };
        toggle.Click += (_, _) =>
        {
            _openWindow = open ? null : index;
            Build();
        };

        var row = new StackPanel { Children = { toggle } };
        if (open) row.Children.Add(WindowDetails(index));
        return row;
    }

    private Control WindowDetails(int index, Thickness? margin = null)
    {
        var window = _draft.Windows[index];
        var recipe = window.EffectiveRecipe();
        void Set(Func<LaunchRecipe, LaunchRecipe> change) => Edit(WithWindow(index, w => w.WithRecipe(change(w.EffectiveRecipe()))));
        void SetAndRebuild(Func<LaunchRecipe, LaunchRecipe> change) => Restructure(WithWindow(index, w => w.WithRecipe(change(w.EffectiveRecipe()))));

        var body = new StackPanel { Spacing = 12, Margin = margin ?? new Thickness(56, 0, 14, 16) };

        if (recipe.Launches)
        {
            var args = Field(recipe.Arguments, true, t => Set(r => r with { Arguments = t }));
            var argsBox = Labelled("Arguments", args);
            var becomes = new TextBlock { FontFamily = Mono, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            Derive(() =>
            {
                var e = Placeholders.Expand(_draft.Windows[index].EffectiveRecipe().Arguments, _values);
                becomes.IsVisible = e.UsesValues;
                Show(becomes, e, prefix: "Becomes ");
            });
            argsBox.Children.Add(becomes);
            body.Children.Add(argsBox);

            if (recipe.StartIn is not null || _startIn.Contains(index))
            {
                body.Children.Add(Labelled("Start in", Field(recipe.StartIn ?? "", true,
                    t => Set(r => r with { StartIn = string.IsNullOrWhiteSpace(t) ? null : t }))));
            }
            else
            {
                body.Children.Add(Link("Start in a particular folder", () =>
                {
                    _startIn.Add(index);
                    Build();
                }));
            }
        }
        else
        {
            body.Children.Add(Muted(recipe.Kind == LaunchKind.Skip
                ? "Left out when the flock loads."
                : "Never started — Flamingle waits for a matching window that's already open and puts it in place."));
        }

        var more = _more.Contains(index);
        body.Children.Add(Link((more ? "▾ " : "▸ ") + "More options", () =>
        {
            if (!_more.Remove(index)) _more.Add(index);
            Build();
        }));

        if (more)
        {
            var options = new StackPanel { Spacing = 12 };
            options.Children.Add(Labelled("How it's opened",
                Segmented(KindLabels, Array.IndexOf(KindOrder, recipe.Kind), i => SetAndRebuild(r => r with { Kind = KindOrder[i] }))));
            if (recipe.Launches)
            {
                options.Children.Add(Labelled(recipe.Kind == LaunchKind.StoreApp ? "App id" : "Program",
                    Field(recipe.Target, true, t => Set(r => r with { Target = t }))));
            }
            options.Children.Add(Labelled("Recognise its window by title containing",
                Field(recipe.TitleContains ?? "", true, t => Set(r => r with { TitleContains = string.IsNullOrWhiteSpace(t) ? null : t })),
                $"Leave empty for any {window.ProcessName} window."));

            var ifOpen = Labelled("If one's already open",
                Segmented(IfOpenLabels, Array.IndexOf(IfOpenOrder, recipe.IfOpen), i => SetAndRebuild(r => r with { IfOpen = IfOpenOrder[i] })));
            var wait = new NumericUpDown
            {
                Minimum = 1, Maximum = LaunchRecipe.MaxWaitSeconds, Increment = 5, Value = recipe.WaitSeconds, Width = 130, FormatString = "0",
            };
            wait.ValueChanged += (_, e) =>
            {
                if (e.NewValue is { } v) Set(r => r with { WaitSeconds = (int)v });
            };
            var pair = new WrapPanel();
            ifOpen.Margin = new Thickness(0, 0, 24, 0);
            pair.Children.Add(ifOpen);
            pair.Children.Add(Labelled("Wait up to (seconds)", wait));
            options.Children.Add(pair);

            body.Children.Add(new Border { Background = Palette.Panel, CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Child = options });
        }

        var changed = new WrapPanel();
        changed.Children.Add(Muted("Changed since it was saved. ", center: true));
        changed.Children.Add(Link("Undo changes", () => Restructure(WithWindow(index, w => w.AsCaptured()))));
        Derive(() => changed.IsVisible = _draft.Windows[index].IsChanged);
        body.Children.Add(changed);

        body.Children.Add(Link("Remove from this flock", () => RemoveWindow(index), Warn));
        return body;
    }

    private SavedFlock WithWindow(int index, Func<SavedWindow, SavedWindow> change)
        => _draft with { Windows = _draft.Windows.Select((w, i) => i == index ? change(w) : w).ToList() };

    // ---- Test load ----

    private Control TestLoad()
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Heading("Test load"));
        body.Children.Add(Muted("What loading asks, and what it then does. Nothing starts from here."));

        if (_draft.InputList.Count == 0)
            body.Children.Add(Muted("Nothing to ask — loading starts straight away."));
        foreach (var input in _draft.InputList)
        {
            if (input.Kind == InputKind.Computed)
            {
                var box = Labelled(input.Label, Muted("Worked out when it loads"));
                box.Children.Add(new TextBlock { Text = input.Default, FontFamily = Mono, FontSize = 11, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap });
                body.Children.Add(box);
                continue;
            }
            var name = input.Name;
            var value = _testValues.TryGetValue(name, out var v) ? v : input.DefaultValue();
            body.Children.Add(Labelled(input.Label, Field(value, true, t =>
            {
                _testValues[name] = t;
                Refresh();
            })));
        }

        var steps = new StackPanel { Spacing = 6 };
        Derive(() =>
        {
            steps.Children.Clear();
            var plan = LoadPlan.Build(_draft, _values);
            for (var i = 0; i < plan.Windows.Count; i++)
            {
                steps.Children.Add(new TextBlock
                {
                    Text = $"{i + 1}. {plan.Windows[i].Describe()}", FontSize = 13, Foreground = Code, TextWrapping = TextWrapping.Wrap,
                });
            }
        });
        body.Children.Add(Labelled("Then", steps));

        body.Children.Add(Link(_showStored ? "Hide what's stored" : "Show what's stored (saved-flocks.json)", () =>
        {
            _showStored = !_showStored;
            Build();
        }));
        if (_showStored)
        {
            var json = new SelectableTextBlock { FontFamily = Mono, FontSize = 11, Foreground = Code };
            Derive(() => json.Text = JsonFile<SavedFlock>.ToJson(_draft));
            body.Children.Add(new Border
            {
                Background = Palette.NavBg, CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
                Child = new ScrollViewer { MaxHeight = 360, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = json },
            });
        }
        return Card(body);
    }

    // ---- Building blocks ----

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = Palette.Title, VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Muted(string text, bool center = false) => new()
    {
        Text = text, FontSize = 13, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = center ? VerticalAlignment.Center : VerticalAlignment.Top,
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text, FontSize = 12, Foreground = Palette.Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0),
    };

    private static Border Card(Control child) => new()
    {
        Background = Palette.Row, CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = child,
    };

    private static Border Pill(string text, IBrush background, IBrush foreground) => new()
    {
        Background = background, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1), VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 6, 0),
        Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground },
    };

    private static Button Link(string text, Action onClick, IBrush? color = null)
    {
        var button = new Button
        {
            Content = text, Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0, 2),
            Foreground = color ?? Palette.AccentBrush, FontSize = 13, Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A text box that reports every change (the draft follows as you type).</summary>
    private static TextBox Field(string text, bool mono, Action<string> changed)
    {
        var box = new TextBox { Text = text, FontSize = mono ? 12 : 13 };
        if (mono) box.FontFamily = Mono;
        box.TextChanged += (_, _) => changed(box.Text ?? "");
        return box;
    }

    private static StackPanel Labelled(string label, Control control, string? hint = null)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.Medium, Foreground = Palette.Muted });
        panel.Children.Add(control);
        if (hint is not null) panel.Children.Add(Hint(hint));
        return panel;
    }

    private static void Plain(TextBlock block, string text)
    {
        block.Inlines = null;
        block.Text = text;
        block.Foreground = Palette.Muted;
    }

    /// <summary>An expanded template: input values highlighted, unknown names in orange.</summary>
    private static void Show(TextBlock block, Expansion e, string? prefix = null)
    {
        var inlines = new InlineCollection();
        if (prefix is not null) inlines.Add(new Run(prefix) { Foreground = Palette.Muted, FontFamily = FontFamily.Default });
        foreach (var s in e.Segments)
        {
            inlines.Add(s.Kind switch
            {
                SegmentKind.Value => new Run(s.Text) { Foreground = Palette.AccentBrush, Background = ValueBg },
                SegmentKind.Unknown => new Run(s.Text) { Foreground = Warn, TextDecorations = TextDecorations.Underline },
                _ => new Run(s.Text) { Foreground = Code },
            });
        }
        block.Inlines = inlines;
    }
}
