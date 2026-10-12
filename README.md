# Gaggle

Group Windows app windows so they minimise, restore, move and relaunch together. See
[docs/scope.md](docs/scope.md). **Experimental — Phase 1 spike (group & minimise).**

## Run

```
run.bat                      # dev instance (pink tray icon, data in %APPDATA%\Gaggle (Dev))
dotnet test gaggle.slnx
.\src\Gaggle.App\bin\Debug\net10.0-windows\gaggle.exe windows | Out-String   # list windows + snap-group guesses
```

## Using it

| Action | How |
|---|---|
| New gaggle | `Alt+Shift+G` (pre-checks the windows snapped with the focused one), or left-click the tray icon |
| Minimise / restore the focused window's gaggle (else the last-used one) | `Alt+Shift+M` |
| Minimise / restore gaggle *n* | `Alt+Shift+1` … `Alt+Shift+9` |
| Members, dissolve, experiments | Right-click the tray icon |

### Zones (FancyZones-style)

Hold **Shift** while dragging any window and the zones appear on every monitor. Drop the window to snap
it into the zone under the cursor. Hold **Ctrl** as well to span from one zone to another. Release
Shift before dropping to place the window normally. Pick each monitor's layout under tray → Zones. The
templates are 2/3 columns, priority 25/50/25, main + side stack, 2×2 and 2×3 grids, and 2 rows.

**The layout tab.** Each monitor's zones act as one container. While its windows are on screen, a
small pull-tab hangs from the layout's top edge. It sits just above the layout's windows, so another
app in front covers it.

**Changing the splits.**
- **Resize a zoned window by an inner edge** (its normal resize border), and the windows on the other
  side follow live, like Windows 11 Snap Groups. A split runs as far as the zones that share it: in a
  2×2 the middle line moves all four windows, while in main + stack the horizontal line only splits the
  stack. Outer edges snap back on release. You can turn this off in tray → Experiments.
- Or use **Adjust splits** in the tab's panel to show draggable strips over every split. Click **Done
  adjusting**, or switch to another app, to hide them.
- The layout remembers the new split. Double-click a strip, use **Even out splits**, or pick a preset
  to reset it.
- **Drag** the tab to move the whole layout, and every window in it follows. Drop it on another
  monitor to move the layout there, as long as that monitor doesn't already have one.
- **Click** the tab to open a panel. The panel floats above everything until it closes. It has:
  - layout preset thumbnails (2–4 columns, 2×2, 2×3 and 3×3 grids, rows, priority, main + stack,
    half + 2×2, wide centre, focus)
  - **Fill with open windows**: drops the monitor's other visible windows into the free zones
  - **Gaps between windows** on/off
  - **Fill screen**, **Minimise layout**, **Adjust splits**, **Even out splits** and **Release windows** (each window goes back to the size
    and place it had before it was snapped)
- **Double-click** the tab to make the layout fill the monitor again.

The same presets, plus **Fill with open windows**, are in tray → Zones → *monitor*.

### Taskbar buttons

Each gaggle gets its own taskbar button, titled with its name, with an icon tiled from its apps' icons.
Clicking it minimises or restores the gaggle. "Close window" on it dissolves the gaggle; the windows
stay open. You can turn this off in tray → Experiments.

With **Zoned windows form a gaggle** on (the default), every window you drop into zones on a monitor
joins that monitor's zone gaggle. The whole zoned layout then minimises and restores together. Dragging
a window back out of its zones takes it out of the zone gaggle, and it goes back to the size it had
before it was snapped. Windows already in a gaggle you picked
by hand stay in it.

### Toggling

A toggle press first **brings the gaggle forward** if any of it is minimised or hidden behind another
app. The next press minimises it.

**Experiments** (tray → Experiments):
- **Linked minimise** (on by default). Minimising or restoring any one member, from its title bar or the
  taskbar, takes the rest of the gaggle with it.
- **Put windows back exactly after restore** (on by default). Corrects members whose app restored to a
  different position.

Hotkeys are in `settings.json` (tray → Open data folder). Restart Gaggle after editing it.

Gaggles are made of live windows. They survive a Gaggle restart while the windows stay open. Saved
layouts that relaunch apps come in Phase 3.
