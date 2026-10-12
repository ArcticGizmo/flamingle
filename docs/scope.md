# Gaggle — Scope

> Group Windows app windows so they minimise, restore, move and relaunch together.

## Problem

Windows 11 Snap Layouts make it easy to arrange windows into a grid, and Snap Groups let you
*restore* a snapped set from the taskbar. But:

- There is no way to **minimise a group** as a unit — each window has to be minimised individually
  (or everything hidden with Win+D, which isn't group-aware).
- There is no way to **move a group** — dragging one window breaks it out of the layout.
- Once the apps are closed (or after a reboot) the layout is gone and has to be **rebuilt by hand**.

## Goal

A lightweight tray app that lets you treat a set of windows as a single parent "container"
(a *gaggle*): one action minimises, restores, moves or relaunches every member.

## Non-goals

- Not a tiling window manager (no auto-tiling, no replacing the Windows shell).
- Not a general PowerToys/FancyZones replacement — inspiration only, no dependency on PowerToys.
- Not restoring in-app state (open tabs, documents, scroll positions).
- No cross-machine sync (for now).

## Features

### Phase 1 — Group & minimise (MVP)

- Tray icon + global hotkey.
- **Create a gaggle** from the windows currently snapped together (or from a picker / click-to-add mode).
- **Minimise / restore** the whole gaggle with one hotkey or tray click.
- Restoring brings members back in the right Z-order and positions.
- Auto-remove members when their window closes.
- Multiple gaggles, each with a name.

### Phase 2 — Move together

- Dragging any member moves the whole gaggle (or a modifier-key + drag to move the group).
- Move a gaggle to another monitor while keeping its layout (scaled per monitor DPI).
- Optional visual hint (outline / badge) showing which windows belong to which gaggle.

### Phase 3 — Save & relaunch

- Save a gaggle as a named **layout** (apps + positions + monitor).
- Relaunch a layout from nothing: start missing apps, wait for their windows, place them.
- Re-attach to already-running instances instead of launching duplicates.
- Support Win32 apps (exe path + args) and Store/UWP apps (AppUserModelID).

### Zones (pulled forward, experimental)

FancyZones-style drag-to-snap. This is inspiration only and doesn't depend on PowerToys.

- Hold a modifier (Shift) while dragging and the zones show on every monitor. Drop snaps the window
  into a zone; Ctrl spans several zones.
- Each monitor gets one of a set of built-in layout templates, chosen from the tray. No editor yet.
- Windows dropped into zones on a monitor form that monitor's *zone gaggle*. This is a toggle; it
  replaces guessing Snap Groups.
- Later: a layout editor, keyboard moves between zones, and the zone gaggle moving as a unit (Phase 2).

### Later / maybe

- Auto-detect new Snap Groups and offer to turn them into a gaggle.
- Per-virtual-desktop gaggles.
- Start-up layouts (restore a layout at login).
- CLI for scripting (`gaggle restore work`).

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
and `../perch` (`Gaggle.Core` pure/testable, `Gaggle.App` Avalonia + Win32, `Gaggle.Tests` xunit).

## Known risks

1. **Feedback loops** — moving member B in response to member A fires events for B. Must suppress self-initiated changes.
2. **Elevated windows (UIPI)** — a non-elevated Gaggle can't control admin windows. Detect and warn rather than fail silently.
3. **Uncooperative apps** — Electron apps, games, and apps that enforce min sizes or restore their own geometry.
4. **Relaunch fidelity** — command-line args aren't reliably recoverable; launch timing varies; some apps spawn a splash window first.
5. **Snap Group detection** — Windows doesn't expose Snap Groups via a public API; inferring membership from geometry is heuristic.
6. **Multi-monitor / DPI changes** — saved coordinates must be stored relative to a monitor and rescaled.

## Decisions

- **Stack:** C# + Avalonia (see above).
- **Creating a gaggle:** explicit picking is the default; "from snap group" is a shortcut that
  pre-checks the windows it *guesses* are snapped together, for the user to confirm.
- **Moving:** modifier-key + drag moves the group; a plain drag moves just the one window.
- **Config:** JSON under `%APPDATA%\Gaggle\` (`Gaggle (Dev)` for dev builds).
- **Membership:** a window belongs to at most one gaggle. Adding it to another moves it.

## Working mode

The feel and behaviour are experimental. Prove things out quickly and well before polishing. Put
behaviours we're unsure about behind a toggle so they can be compared, and don't spend effort on
settings UI, installers or visual polish yet.
