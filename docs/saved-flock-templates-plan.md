# Saved flock templates — implementation plan

How we get from today's capture-only saved flocks to the template designer in the design canvas
(`docs/artefacts/template-designer/`, published at https://claude.ai/artifact/PUttvRnygnGdiPtSNM1vRN): saved
flocks you can edit, that can ask for values when they load, and that actually relaunch.

## Where we are

- `SavedFlock` / `SavedWindow` (Core/Saved) hold what was **captured**: exe, args, AUMID, class, title,
  placement, zone, Z-order, desktop, elevation. Written by `SavedFlockService.Save`.
- Settings → Flocks lists saved flocks; the layout panel's Load button only switches the layout preset.
- Nothing finds, starts or places windows yet.

## Decisions

- **Captured vs recipe.** A saved window keeps its captured fields untouched and gains an optional `Recipe` — how
  to open it *now*. No recipe = "as captured". "Changed since it was saved · Undo changes" = a recipe that differs
  from the captured one; undo = drop the recipe. Old `saved-flocks.json` files load unchanged.
- **Placeholders are `${name}`** (VS Code-style), with `${name.name}` (last path part), `${name.parent}` and
  `${env:VAR}`. `$${` writes a literal `${`. Placeholders work in arguments, start-in folder and the
  title-to-recognise-it-by.
- **Inputs live on the saved flock**: name, label, kind (Text, Folder, File, Number, Choice, Computed), default,
  remember-recent, and the recent values themselves.
- **Renaming an input renames its uses** in every window, so "Written in commands as" never breaks a flock.
- **Computed inputs run a command** at load (stdout's first line, trimmed, short timeout), may use earlier inputs,
  and are shown in the load prompt before anything runs. Cycles are a problem, not a hang.
- **Matching by exe/AUMID, never PID** — single-instance apps hand their window to an existing process.
- Pure logic in Core with xunit tests; Win32 (launching, waiting, placing) in App.

## Phases

### A. Core: model and engine — *done* (`Core/Saved`, tests in `SavedFlockTemplateTests`)

1. Model: `FlockInput`, `InputKind`, `LaunchRecipe` (`LaunchKind` Program / StoreApp / AlreadyOpen / Skip;
   target, args, start-in, title-contains, `IfAlreadyOpen` UseIt / StartAnother / Ask, wait seconds).
   `SavedWindow.Recipe` (nullable) + `EffectiveRecipe()`; `SavedFlock.Inputs`.
2. `Placeholders`: expand a template against values → text, display segments (plain / filled / unknown) and the
   unknown names; list the names a template uses; rename a name.
3. `InputValues`: resolve every input's value in dependency order (defaults, user-entered values, computed results
   supplied by a callback), reporting cycles.
4. `FlockProblems`: unknown names, invalid / duplicate input names, computed cycles, a launch with no target.
5. `InputSuggester`: the folder path most windows share (args / start-in, not the exe), excluding dismissed
   ones; `MakeInput` swaps it for `${name}` everywhere and adds a Folder input. (Later: ports, `file:///` URIs.)
6. `LoadPlan`: a saved flock + values → per window the resolved launch (exe or AUMID, args, start-in), the title
   to match, and a one-line description of what loading will do. Drives the designer's Test load and the real
   relaunch alike.

### B. The designer (Settings → Flocks → Edit)

- An **Edit** button on each saved flock card opens an editor page inside the Settings window (breadcrumb back to
  Flocks), working on a copy until **Save changes**.
- Built progressively, as in the canvas: *Asks for* (empty state, suggestion card, input chips with one editor
  open), *Starts* (one row per window, one open at a time, *More options*), *Undo changes* per window.
- **Test load** side panel from `LoadPlan`, with "Show what's stored". Nothing launches.
- `SavedFlockService.Update` persists edits; renaming the flock re-keys the "same name replaces" rule.

### C. Relaunch (steps 2–3 of the original plan)

- `RelaunchService`: for each planned window — Skip; find an open window matching exe/AUMID (+ title); per
  `IfAlreadyOpen` use it or start another; start it (`Process.Start` with args and working directory, or
  `IApplicationActivationManager.ActivateApplication` for Store apps); wait (show / foreground hooks, up to the
  window's wait time) for a matching new window that has settled (title and size stable — skips splash screens).
- Place it: into its zones via a new `ZoneContainerService` entry point, else its saved rect remapped to today's
  monitors (`ZoneContainer.MoveBetween`), maximised / minimised as saved, then Z-order and virtual desktop.
- The placed windows become a live flock with the saved flock's name. Toast what didn't show up in time.
- Wire the layout panel's Load (no inputs) and a Load button on Settings' saved flock cards.

### D. Loading with inputs

- The layout panel's load prompt (canvas artboard *Loading it from the layout panel*): one field per input, recent
  values as chips, Browse for Folder / File, computed inputs shown read-only with their command, a one-line
  summary of what will start, Load / Cancel. Uses the panel's existing typing mode.
- Recent values saved back to the flock after a load (newest first, five kept).
- Computed inputs run here, after the values they depend on are known.

### E. Later

- Suggestions for ports and `file:///` URIs (VS Code captures `--folder-uri`); "Ask" for already-open windows;
  editing placement from the designer; `flamingle load "<flock>" --worktree …` from a CLI; loading the same flock
  twice side by side (one per worktree).

## Risks

- **Captured arguments aren't always reusable** (single-instance apps record their first launch's args). The
  designer exists partly to fix this by hand.
- **Explorer windows** all live in one process; their folder needs `IShellWindows` (phase E).
- **Elevated apps**: launching prompts UAC and a non-elevated Flamingle can't place them; flag, don't fail.
- **Computed inputs run commands**: keep them visible in the load prompt and time-limited.
