using Flamingle.Core.Geometry;
using Flamingle.Core.Persistence;
using Flamingle.Core.Saved;

namespace Flamingle.Tests;

public sealed class SavedFlockTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "flamingle-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static readonly IReadOnlyDictionary<string, string> NoValues = new Dictionary<string, string>();

    private static SavedWindow Window(string process, string? args = "", int z = 0, bool packaged = false,
        string? exe = "auto", LaunchRecipe? recipe = null, SavedZone? zone = null) => new(
        process,
        exe == "auto" ? (packaged ? @"C:\Program Files\WindowsApps\x\app.exe" : $@"C:\Apps\{process}.exe") : exe,
        args,
        packaged ? $"{process}_8wekyb3d8bbwe!App" : null,
        packaged,
        "Window",
        $"{process} title",
        new SavedPlacement(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1400), 1.0,
            new PixelRect(10, 20, 1200, 900), null, Maximised: false, Minimised: false),
        z,
        Zone: zone,
        Recipe: recipe);

    private static SavedFlock Flock(IEnumerable<SavedWindow> windows, params FlockInput[] inputs)
        => new(Guid.NewGuid(), "Dev", DateTimeOffset.UnixEpoch, [.. windows], inputs.Length == 0 ? null : [.. inputs]);

    private static Dictionary<string, string> Values(params (string Name, string Value)[] pairs)
        => pairs.ToDictionary(p => p.Name, p => p.Value);

    // ---- Placeholders ----

    [Fact]
    public void Expand_fills_values_and_splits_segments()
    {
        var e = Placeholders.Expand("--new-window \"${worktree}\"", Values(("worktree", @"C:\src\app")));

        Assert.Equal(@"--new-window ""C:\src\app""", e.Text);
        Assert.Equal(
            [new Segment("--new-window \"", SegmentKind.Text), new Segment(@"C:\src\app", SegmentKind.Value), new Segment("\"", SegmentKind.Text)],
            e.Segments);
        Assert.Empty(e.Unknown);
        Assert.True(e.UsesValues);
    }

    [Fact]
    public void Expand_leaves_unknown_names_as_written()
    {
        var e = Placeholders.Expand("-d ${wrktree} ${wrktree}", NoValues);
        Assert.Equal("-d ${wrktree} ${wrktree}", e.Text);
        Assert.Equal(["wrktree"], e.Unknown);
        Assert.Equal(SegmentKind.Unknown, e.Segments[1].Kind);
    }

    [Fact]
    public void Expand_supports_name_parent_env_escapes_and_any_case()
    {
        var values = Values(("Worktree", @"C:\src\app-two\"));
        var env = (string v) => v == "HOME" ? @"C:\Users\me" : null;

        Assert.Equal("app-two", Placeholders.Expand("${worktree.name}", values, env).Text);
        Assert.Equal(@"C:\src", Placeholders.Expand("${WORKTREE.parent}", values, env).Text);
        Assert.Equal(@"C:\Users\me", Placeholders.Expand("${env:HOME}", values, env).Text);
        Assert.Equal(["env:NOPE"], Placeholders.Expand("${env:NOPE}", values, env).Unknown);
        Assert.Equal("${worktree} costs $5", Placeholders.Expand("$${worktree} costs $5", values, env).Text);
    }

    [Fact]
    public void NamesIn_lists_inputs_once_and_skips_env()
        => Assert.Equal(["a", "b"], Placeholders.NamesIn("${a} ${b.name} ${A} ${env:PATH} $${c}"));

    [Fact]
    public void Rename_keeps_parts_and_leaves_other_names()
        => Assert.Equal("${tree} ${tree.name} ${other} ${env:worktree}",
            Placeholders.Rename("${worktree} ${Worktree.name} ${other} ${env:worktree}", "worktree", "tree"));

    // ---- Recipes ----

    [Fact]
    public void Captured_recipe_follows_what_was_captured()
    {
        Assert.Equal(new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", "-n"), Window("Code", "-n").EffectiveRecipe());
        Assert.Equal(new LaunchRecipe(LaunchKind.StoreApp, "wt_8wekyb3d8bbwe!App", ""), Window("wt", packaged: true).EffectiveRecipe());
        Assert.Equal(LaunchKind.AlreadyOpen, Window("x", null, exe: null).EffectiveRecipe().Kind);
    }

    [Fact]
    public void A_recipe_matching_the_capture_is_dropped()
    {
        var w = Window("Code", "-n");
        var edited = w.WithRecipe(w.EffectiveRecipe() with { Arguments = "-n ${folder}" });
        Assert.True(edited.IsChanged);

        var back = edited.WithRecipe(edited.EffectiveRecipe() with { Arguments = "-n" });
        Assert.Null(back.Recipe);
        Assert.False(back.IsChanged);
        Assert.Null(edited.AsCaptured().Recipe);
    }

    [Fact]
    public void Old_files_load_and_new_fields_round_trip()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "saved-flocks.json");
        File.WriteAllText(path, """
            { "Flocks": [ { "Id": "6f1c1d0e-0000-0000-0000-000000000001", "Name": "Old", "SavedAt": "2026-10-15T00:00:00+00:00",
              "Windows": [ { "ProcessName": "Code", "ExePath": "C:\\Apps\\Code.exe", "Arguments": "-n", "AppUserModelId": null,
                "Packaged": false, "ClassName": "W", "Title": "t", "ZIndex": 0,
                "Placement": { "Monitor": "D1", "WorkArea": { "X": 0, "Y": 0, "Width": 10, "Height": 10 }, "Scale": 1,
                  "Restored": { "X": 0, "Y": 0, "Width": 5, "Height": 5 }, "Visible": null, "Maximised": false, "Minimised": false } } ] } ] }
            """);
        var file = new JsonFile<SavedFlocks>(path);
        var old = Assert.Single(file.Load(SavedFlocks.Empty).Flocks);
        Assert.Empty(old.InputList);
        Assert.Null(old.Windows[0].Recipe);

        var edited = old with
        {
            Inputs = [new FlockInput("worktree", "Worktree", InputKind.Folder, @"C:\src", RememberRecent: true, Recent: [@"C:\a"])],
            Windows = [old.Windows[0].WithRecipe(new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", "${worktree}",
                StartIn: "${worktree}", TitleContains: "${worktree.name}", IfOpen: IfAlreadyOpen.StartAnother, WaitSeconds: 30))],
        };
        Assert.True(file.Save(new SavedFlocks([edited])));
        Assert.DoesNotContain("IsChanged", File.ReadAllText(path));

        var loaded = Assert.Single(file.Load(SavedFlocks.Empty).Flocks);
        Assert.Equal(edited.Windows[0].Recipe, loaded.Windows[0].Recipe);
        var input = Assert.Single(loaded.InputList);
        Assert.Equal((InputKind.Folder, true, @"C:\a"), (input.Kind, input.RememberRecent, input.Recent![0]));
    }

    // ---- Inputs ----

    [Fact]
    public void Choices_and_recent_values()
    {
        var choice = new FlockInput("env", "Environment", InputKind.Choice, " dev, test ,, prod ");
        Assert.Equal(["dev", "test", "prod"], choice.Choices());
        Assert.Equal("dev", choice.DefaultValue());

        var folder = new FlockInput("f", "Folder", InputKind.Folder, RememberRecent: true);
        for (var i = 1; i <= 6; i++) folder = folder.WithRecent($@"C:\p{i}");
        folder = folder.WithRecent(@"c:\P3");
        Assert.Equal([@"c:\P3", @"C:\p6", @"C:\p5", @"C:\p4", @"C:\p2"], folder.Recent);

        var forgetful = new FlockInput("f", "Folder", InputKind.Folder);
        Assert.Same(forgetful, forgetful.WithRecent("x"));
    }

    [Fact]
    public void Renaming_an_input_renames_its_uses()
    {
        var flock = Flock(
            [Window("Code", recipe: new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", "${worktree}", TitleContains: "${worktree.name}"))],
            new FlockInput("worktree", "Worktree", InputKind.Folder),
            new FlockInput("branch", "Branch", InputKind.Computed, "git -C \"${worktree}\" branch --show-current"));

        var renamed = flock.RenameInput("worktree", "tree");

        Assert.Equal(["tree", "branch"], renamed.InputList.Select(i => i.Name));
        Assert.Equal("git -C \"${tree}\" branch --show-current", renamed.Input("branch")!.Default);
        var recipe = renamed.Windows[0].EffectiveRecipe();
        Assert.Equal(("${tree}", "${tree.name}"), (recipe.Arguments, recipe.TitleContains));
        Assert.Empty(FlockProblems.Find(renamed));
    }

    [Fact]
    public void Resolve_uses_entered_values_then_defaults_then_computed_in_order()
    {
        FlockInput[] inputs =
        [
            new("label", "Label", InputKind.Computed, "describe ${branch}"),   // depends on a later computed input
            new("branch", "Branch", InputKind.Computed, "git -C ${worktree} branch"),
            new("worktree", "Worktree", InputKind.Folder, @"C:\src\app"),
            new("port", "Port", InputKind.Number, "5173"),
        ];
        var ran = new List<string>();
        var resolved = InputValues.Resolve(inputs, Values(("worktree", @" C:\src\two "), ("port", "  ")), cmd =>
        {
            ran.Add(cmd);
            return cmd.StartsWith("git") ? "\n  feature/x  \nmore" : "described";
        });

        Assert.Equal(@"C:\src\two", resolved.Values["worktree"]);
        Assert.Equal("5173", resolved.Values["port"]);
        Assert.Equal("feature/x", resolved.Values["branch"]);
        Assert.Equal("described", resolved.Values["label"]);
        Assert.Equal([@"git -C C:\src\two branch", "describe feature/x"], ran);
        Assert.Empty(resolved.Failed);
    }

    [Fact]
    public void Resolve_reports_failed_and_looping_computed_inputs()
    {
        FlockInput[] inputs =
        [
            new("broken", "Broken", InputKind.Computed, "fail"),
            new("a", "A", InputKind.Computed, "x ${b}"),
            new("b", "B", InputKind.Computed, "y ${a}"),
        ];
        var resolved = InputValues.Resolve(inputs, NoValues, _ => null);

        Assert.Equal(["broken", "a", "b"], resolved.Failed);
        Assert.Equal("", resolved.Values["a"]);
        Assert.Equal(["a", "b"], InputValues.InLoops(inputs));
    }

    // ---- Problems ----

    [Fact]
    public void A_well_formed_flock_has_no_problems()
    {
        var flock = Flock(
            [
                Window("Code", recipe: new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", "${worktree}")),
                Window("Gone", recipe: new LaunchRecipe(LaunchKind.Skip, "", "${nonsense}")),
            ],
            new FlockInput("worktree", "Worktree", InputKind.Folder));
        Assert.Empty(FlockProblems.Find(flock));
    }

    [Fact]
    public void Problems_cover_names_unknowns_loops_targets_and_waits()
    {
        var flock = Flock(
            [
                Window("Code", recipe: new LaunchRecipe(LaunchKind.Program, "", "${wrktree}", WaitSeconds: 0)),
                Window("wt", recipe: new LaunchRecipe(LaunchKind.StoreApp, "", "")),
            ],
            new FlockInput("1st", "First", InputKind.Text),
            new FlockInput("dup", "Dup", InputKind.Text),
            new FlockInput("DUP", "Dup again", InputKind.Text),
            new FlockInput("a", "A", InputKind.Computed, "${b}"),
            new FlockInput("b", "B", InputKind.Computed, "${a} ${missing}"),
            new FlockInput("pick", "Pick", InputKind.Choice, " , "));

        var messages = FlockProblems.Find(flock).Select(p => p.Message).ToList();

        Assert.Contains("\"1st\" can't be an input name — use letters, digits and _, not starting with a digit.", messages);
        Assert.Contains("Two inputs are called \"DUP\".", messages);
        Assert.Contains("\"B\" uses ${missing}, which isn't an input.", messages);
        Assert.Contains("a and b each need the other to be worked out first.", messages);
        Assert.Contains("\"Pick\" has no choices.", messages);
        Assert.Contains("Code has no program to start.", messages);
        Assert.Contains("Code's wait must be between 1 and 300 seconds.", messages);
        Assert.Contains("Code uses ${wrktree}, which isn't an input.", messages);
        Assert.Contains("wt has no app id to start.", messages);
    }

    // ---- Suggestions ----

    [Fact]
    public void Suggests_the_folder_most_windows_share()
    {
        var flock = Flock(
        [
            Window("Code", @"--new-window ""C:\src\app"" --log C:\logs\x.txt"),
            Window("wt", @"-d C:\src\app\"),
            Window("Edge", "--new-window http://localhost:5173"),
        ]);

        Assert.Equal(new InputSuggestion(@"C:\src\app", 2), InputSuggester.Suggest(flock, []));
        Assert.Equal(new InputSuggestion(@"C:\logs\x.txt", 1), InputSuggester.Suggest(flock, [@"c:\SRC\app"]));
        Assert.Null(InputSuggester.Suggest(flock, [@"C:\src\app", @"C:\logs\x.txt"]));
    }

    [Fact]
    public void Making_an_input_replaces_whole_uses_only()
    {
        var flock = Flock(
            [
                Window("Code", @"""C:\src\app"" C:\src\app-two C:\SRC\APP\bin",
                    recipe: new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", @"""C:\src\app"" C:\src\app-two C:\SRC\APP\bin", StartIn: @"C:\src\app")),
            ],
            new FlockInput("folder", "Taken", InputKind.Text));

        var made = InputSuggester.MakeInput(flock, @"C:\src\app\");

        var input = made.InputList[1];
        Assert.Equal(("folder2", InputKind.Folder, @"C:\src\app", true), (input.Name, input.Kind, input.Default, input.RememberRecent));
        var recipe = made.Windows[0].EffectiveRecipe();
        Assert.Equal(@"""${folder2}"" C:\src\app-two ${folder2}\bin", recipe.Arguments);
        Assert.Equal("${folder2}", recipe.StartIn);
    }

    // ---- Re-saving and editing ----

    [Fact]
    public void Re_saving_keeps_inputs_dismissals_and_each_apps_edits()
    {
        var first = Window("Code", "-n", recipe: new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", "${folder} --one"));
        var second = Window("Code", "-n", recipe: new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Code.exe", "${folder} --two"));
        var edited = Flock([first, Window("Edge"), second], new FlockInput("folder", "Folder", InputKind.Folder)) with
        {
            Dismissed = [@"C:\logs"],
        };
        var saved = SavedFlocks.Empty.Save(edited, out _);

        var recaptured = new SavedFlock(Guid.NewGuid(), "dev", DateTimeOffset.Now,
            [Window("Code", "-n"), Window("Code", "-n"), Window("Terminal")]);
        var after = Assert.Single(saved.Save(recaptured, out var replaced).Flocks);

        Assert.True(replaced);
        Assert.Equal(edited.Id, after.Id);
        Assert.Equal(["folder"], after.InputList.Select(i => i.Name));
        Assert.Equal([@"C:\logs"], after.DismissedList);
        Assert.Equal(["${folder} --one", "${folder} --two", ""], after.Windows.Select(w => w.EffectiveRecipe().Arguments));
        Assert.Equal(3, after.Windows.Count);
    }

    [Fact]
    public void Update_replaces_by_id()
    {
        var a = Flock([Window("A")]);
        var b = Flock([Window("B")]) with { Name = "Other" };
        var saved = SavedFlocks.Empty.Save(a, out _).Save(b, out _);

        var updated = saved.Update(a with { Name = "Renamed" });

        Assert.Equal(["Renamed", "Other"], updated.Flocks.Select(f => f.Name));
    }

    // ---- Load plan ----

    [Fact]
    public void Load_plan_fills_in_each_window_topmost_first()
    {
        var flock = Flock(
            [
                Window("Edge", z: 1, recipe: new LaunchRecipe(LaunchKind.Program, @"C:\Apps\Edge.exe", "http://localhost:${port}",
                    TitleContains: "localhost:${port}", IfOpen: IfAlreadyOpen.UseIt)),
                Window("Code", z: 0, zone: new SavedZone("columns-2", [0]),
                    recipe: new LaunchRecipe(LaunchKind.Program, @"${worktree}\tools\code.exe", "${worktree} ${oops}",
                        StartIn: "${worktree}", TitleContains: "  ", IfOpen: IfAlreadyOpen.StartAnother)),
            ],
            new FlockInput("worktree", "Worktree", InputKind.Folder),
            new FlockInput("port", "Port", InputKind.Number));

        var plan = LoadPlan.Build(flock, Values(("worktree", @"C:\src\two"), ("port", "5174")));

        var code = plan.Windows[0];
        Assert.Equal((@"C:\src\two\tools\code.exe", @"C:\src\two ${oops}", @"C:\src\two", (string?)null),
            (code.Target, code.Arguments, code.StartIn, code.TitleContains));
        Assert.Equal(["oops"], plan.Unknown);
        Assert.Equal("Starts a new Code window, on DISPLAY1 · zone 1 of 2 columns.", code.Describe());

        var edge = plan.Windows[1];
        Assert.Equal("Uses an open Edge window titled \u201Clocalhost:5174\u201D, or starts one, on DISPLAY1 · 1200×900 at 10,20.",
            edge.Describe());
    }
}
