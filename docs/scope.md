# Flamingle — Scope

> Group Windows app windows so they minimise, restore, move and relaunch together.

*Renamed from Gaggle to Flamingle on 2026-10-13; a group of windows is now called a flock (was a gaggle).*

## Problem

Windows 11 Snap Layouts make it easy to arrange windows into a grid, and Snap Groups let you
*restore* a snapped set from the taskbar. But:

- There is no way to **minimise a group** as a unit — each window has to be minimised individually
  (or everything hidden with Win+D, which isn't group-aware).
- There is no way to **move a group** — dragging one window breaks it out of the layout.
- Once the apps are closed (or after a reboot) the layout is gone and has to be **rebuilt by hand**.

## Goal

A lightweight tray app that lets you treat a set of windows as a single parent "container"
(a *flock*): one action minimises, restores, moves or relaunches every member.

## Non-goals

- Not a tiling window manager (no auto-tiling, no replacing the Windows shell).
- Not a general PowerToys/FancyZones replacement — inspiration only, no dependency on PowerToys.
- Not restoring in-app state (open tabs, documents, scroll positions).
- No cross-machine sync (for now).

## Features

### Phase 1 — Group & minimise (MVP)

- Tray icon + global hotkey.
- **Create a flock** from the windows currently snapped together (or from a picker / click-to-add mode).
- **Minimise / restore** the whole flock with one hotkey or tray click.
- Restoring brings members back in the right Z-order and positions.
- Auto-remove members when their window closes.
- Multiple flocks, each with a name.

### Phase 2 — Move together

- Dragging any member moves the whole flock (or a modifier-key + drag to move the group).
- Move a flock to another monitor while keeping its layout (scaled per monitor DPI).
- Optional visual hint (outline / badge) showing which windows belong to which flock.

### Phase 3 — Save & relaunch

- Save a flock as a named **layout** (apps + positions + monitor).
- Relaunch a layout from nothing: start missing apps, wait for their windows, place them.
- Re-attach to already-running instances instead of launching duplicates.
- Support Win32 apps (exe path + args) and Store/UWP apps (AppUserModelID).
- Built in steps: (1) **capture** — a flock's Save button writes `saved-flocks.json` (exe, args, AUMID, class,
  title, monitor + rect, zone, Z-order, desktop), shown in Settings → Flocks so we can judge how much real apps
  give away; (2) re-attach to open windows and place them; (3) launch what's missing and wait for its windows
  (matched by exe/AUMID, not PID — single-instance apps hand off); (4) zones, Z-order, desktops.
- Loading a saved flock from a layout's panel (the Flock row's load button) currently only switches the layout to
  the one it was saved in; it will find / start the apps once steps 2–3 exist.
- **Parameterised flocks (wanted):** a saved flock can declare variables (e.g. a git worktree path, a branch, a
  project folder) that its windows' launch recipes use — in arguments, working directories, even titles to match
  — so one saved flock can be loaded for different values (open the editor, terminal and browser for *this*
  worktree). Loading asks for the values (remembering recent ones). Design the saved-window recipe so args and
  working directory can hold placeholders from the start.

### Zones (pulled forward, experimental)

FancyZones-style drag-to-snap. This is inspiration only and doesn't depend on PowerToys.

- Hold a modifier (Shift) while dragging and the zones show on every monitor. Drop snaps the window
  into a zone; Ctrl spans several zones.
- Each monitor gets one of a set of built-in layout templates, chosen from the tray. No editor yet.
- Dragging a window to the top of a monitor drops down a *snap strip* of layouts (toggle) — Flamingle's take on
  Windows' snap bar, which should be turned off alongside it. Dropping on a zone of another layout switches the
  monitor to that layout.
- Layouts show nothing on screen. The edit-layout shortcut (Alt+Shift+E) works on any monitor, with or without
  windows in its layout (so a layout can be set up before it's used — the frame draws its numbered zones), and puts
  the layout under the cursor into
  *edit mode*: an outline you drag from anywhere to move it (dropping it on a monitor that has a layout swaps the
  two), its split strips, and its panel. The old always-visible pull-tab is gone. The panel also names and saves the
  layout's zone flock (a zone flock the user renames keeps that name when it moves monitor or desktop) and lists
  the saved flocks.
- Windows dropped into zones on a monitor form that monitor's *zone flock*. This is a toggle; it
  replaces guessing Snap Groups.
- Zone flocks are per monitor *per virtual desktop* (toggle, on by default), so zoned windows on
  different desktops never minimise or restore together. A zoned window moved to another desktop moves
  to that desktop's zone flock. Hand-picked flocks are not split by desktop.
- A flock's taskbar button follows it to the desktop most of its windows are on (toggle).
- A flock's taskbar button draws its own preview: the flock's windows captured and laid out as they sit on
  screen (pictures taken just before a minimise stand in for minimised ones), with Minimise / restore and
  Dissolve buttons under it (toggle). Hovering the button outlines the flock's windows on screen (toggle) —
  detected from the taskbar's preview request, since Windows has no "button hovered" event.
- Later: a layout editor, keyboard moves between zones, and the zone flock moving as a unit (Phase 2).

### Later / maybe

- Auto-detect new Snap Groups and offer to turn them into a flock.
- Per-virtual-desktop flocks.
- Start-up layouts (restore a layout at login).
- CLI for scripting (`flamingle restore work`).

## Technical approach

| Concern | Win32 mechanism |
|---|---|
| Discover windows | `EnumWindows`, filter by visible / has-title / not tool-window / not cloaked (`DwmGetWindowAttribute(DWMWA_CLOAKED)`) |
| Watch changes | `SetWinEventHook` — `EVENT_SYSTEM_MINIMIZESTART/END`, `EVENT_OBJECT_LOCATIONCHANGE`, `EVENT_SYSTEM_MOVESIZESTART/END`, `EVENT_OBJECT_DESTROY` |
| Minimise / restore | `ShowWindow` / `SetWindowPlacement` |
| Move as a unit | `BeginDeferWindowPos` / `DeferWindowPos` / `EndDeferWindowPos` (atomic, no flicker) |
| Persist layout | `GetWindowPlacement`, monitor via `MonitorFromWindow` + `GetDpiForMonitor` |
| Identify apps | `GetWindowThreadProcessId` → `QueryFullProcessImageName`; `SHGetPropertyStoreForWindow` → `PKEY_AppUserModel_ID` for Store apps |
| Hotkeys | `RegisterHotKey` |

Process must be **per-monitor DPI aware (v2)**.

### Stack

**C# / .NET 10 + Avalonia 12**, hand-rolled P/Invoke — same layout and conventions as `../shrike`
and `../perch` (`Flamingle.Core` pure/testable, `Flamingle.App` Avalonia + Win32, `Flamingle.Tests` xunit).

## Known risks

1. **Feedback loops** — moving member B in response to member A fires events for B. Must suppress self-initiated changes.
2. **Elevated windows (UIPI)** — a non-elevated Flamingle can't control admin windows. Detect and warn rather than fail silently.
3. **Uncooperative apps** — Electron apps, games, and apps that enforce min sizes or restore their own geometry.
4. **Relaunch fidelity** — command-line args aren't reliably recoverable; launch timing varies; some apps spawn a splash window first.
5. **Snap Group detection** — Windows doesn't expose Snap Groups via a public API; inferring membership from geometry is heuristic.
6. **Multi-monitor / DPI changes** — saved coordinates must be stored relative to a monitor and rescaled.

## Decisions

- **Stack:** C# + Avalonia (see above).
- **Creating a flock:** explicit picking is the default; "from snap group" is a shortcut that
  pre-checks the windows it *guesses* are snapped together, for the user to confirm.
- **Moving:** modifier-key + drag moves the group; a plain drag moves just the one window.
- **Config:** JSON under `%APPDATA%\Flamingle\` (`Flamingle (Dev)` for dev builds).
- **Membership:** a window belongs to at most one flock. Adding it to another moves it.
- **Saving:** an explicit snapshot — "Save" on a flock writes a saved flock (replacing one of the same name).
  Live flocks behave as before and still drop windows that close; a saved flock only changes when re-saved.

## Working mode

The feel and behaviour are experimental. Prove things out quickly and well before polishing. Put
behaviours we're unsure about behind a toggle so they can be compared, and don't spend effort on
settings UI, installers or visual polish yet.
