# Changelog

All notable changes to Flamingle are documented here.
Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

---

## [Unreleased]

---

## [v0.2.2] - 2026-10-30

### Fixed

- Each virtual desktop keeps its own layout (they were sharing, badly)

---

## [v0.2.1] - 2026-10-22

### Added

- Name and version at the top of the tray menu
- Background news goes to Windows notifications (Settings → Experiments)

### Changed

- Far fewer toasts (things that worked no longer announce it)
- Warns once about a window it can't minimise, not every toggle
- Adjust splits greyed out on one-zone layouts (there was never anything to split)

### Fixed

- Loading a saved flock twice no longer steals the first one's windows

---

## [v0.2.0] - 2026-10-21

### Changed

- Installer halved, 50 MB → 25 MB (most of it was .NET we never called)

---

## [v0.1.2] - 2026-10-21

### Added

- Check for updates from the tray menu or Settings → About (previously: reinstall and hope)

---

## [v0.1.1] - 2026-10-21

### Added

- Start Flamingle when you sign in (Settings → Behaviour)
- One-line PowerShell install that checks the checksums first

### Changed

- Zones sit flush; gaps are gone (they were rarely wanted)
- Dropped the new-flock and minimise / restore shortcuts (Alt+Shift+E does the job)

### Fixed

- Picking a layout no longer scrolls Settings → Zones to the top
- Picking a layout takes one click (it took two, for no good reason)

---

## [v0.1.0] - 2026-10-19

First release. Windows that belong together, now kept together.

### Flocks

- Group windows into flocks that minimise and restore together
- New-flock picker (Alt+Shift+G) pre-ticks windows snapped beside yours
- Minimise / restore hotkey (Alt+Shift+M), plus slot hotkeys for flocks 1–9
- Minimising one member takes the rest with it
- Windows go back exactly where they were after a restore
- Flocks survive a Flamingle restart (if their windows do)

### Zones

- Drag-to-snap zones: hold Shift while dragging, Ctrl to span several
- Windows in a monitor's zones form a flock, one per virtual desktop
- Resizing a zoned window moves its neighbours
- Snap strip: drag to the top of a screen to pick a layout
- Edit mode (Alt+Shift+E) on any monitor: move, split, swap, undo
- Adjustable gaps between zones, or none
- Windows pulled out of a layout return to their old size
- Layouts follow side panels as they dock and close
- Zones show on one monitor at a time (USB docks send their thanks)
- Layouts stay with their monitor across wakes and redocks (Windows renumbers them for fun)

### Saved flocks

- Save a flock; relaunch its apps later, windows back in place
- Design saved flocks on screen, or edit them as a form
- Load a saved flock onto any monitor
- Per-window launch recipes: program, Store app, or an already-open window
- Inputs for saved flocks (they take their defaults until the prompt arrives)

### Taskbar

- A taskbar button per flock: click to minimise or restore
- Previews show the flock's windows as laid out on screen
- Hovering the button outlines the flock's windows
- Buttons follow their flock across virtual desktops
- Flamingle icon numbered by monitor, or a mosaic of app icons

### Settings

- Settings window, one click on the tray icon away
- Turn off Windows' own snap layouts (they were here first)
- Changelog page, plus a what's-new window after updates (starting next time)

---
