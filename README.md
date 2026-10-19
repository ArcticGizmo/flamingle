<h1 align="center">Flamingle</h1>

<p align="center">
 <img src="./landing-icon.png" width="150"  />
</p>

<p align="center">
<strong>Windows that flock together</strong>
</p>

<br>

Flamingle is a small Windows tray app that groups app windows into *flocks* so they minimise, restore,
move and relaunch together. It also snaps windows into FancyZones-style layouts. See
[docs/scope.md](docs/scope.md) for the plan. **Still experimental:** the behaviours being tried out can be
switched off in Settings to compare.

## Install

Download `Flamingle-win-Setup.exe` from the [latest release](../../releases/latest) and run it. No admin
rights needed: it installs to `%LocalAppData%\Flamingle\`, adds a Start Menu shortcut and an uninstaller in
Settings → Apps, and starts Flamingle in the tray. Your settings and flocks live separately in
`%APPDATA%\Flamingle`, so uninstalling and reinstalling keeps them.

The installer isn't code-signed yet, so SmartScreen shows **"Windows protected your PC"**. Click
**More info → Run anyway**. To check the download against the release's `SHA256SUMS.txt` first:

```powershell
$want = (Select-String -Path SHA256SUMS.txt -Pattern 'Flamingle-win-Setup.exe').Line.Split()[0]
(Get-FileHash Flamingle-win-Setup.exe -Algorithm SHA256).Hash -eq $want   # True
```

Prefer no installer? `Flamingle-win-Portable.zip` on the same release runs from wherever you extract it.

**Updating.** There's no in-app updater yet. Download the newer release's `Flamingle-win-Setup.exe` and run
it. The first launch after an update lists what's new (Settings → Changelog has the full history).

## Using it

Click the tray icon to open **Settings**, where everything lives. Right-click it for Settings, Open data
folder and Quit.

| Action | How |
|---|---|
| New flock | `Alt+Shift+G` opens a picker with the windows snapped beside the focused one ticked, or Settings → Flocks → **New flock…** |
| Minimise / restore the focused window's flock (else the last-used one) | `Alt+Shift+M` |
| Minimise / restore flock *n* | `Alt+Shift+1` … `Alt+Shift+9` |
| Edit the layout on the monitor under the cursor | `Alt+Shift+E` (again, or Esc, to finish) |
| Rename, save, dissolve | Settings → Flocks |

Change any shortcut in Settings → Shortcuts: click it and press the new combination. The slot modifiers
(`Alt+Shift` for 1–9) are there too.

### Toggling

A toggle press **brings a buried flock forward** first: if any of it is minimised, or covered by another
window, it's restored and raised. A flock that's already in plain view minimises on the first press.

With **Linked minimise** on (the default), minimising or restoring any one member, from its title bar or the
taskbar, takes the rest of the flock with it. Flamingle also puts each window back exactly where it was
after a restore, for apps that restore to their own idea of where they belong.

Flocks are made of live windows. They survive a Flamingle restart while their windows stay open. To bring a
set of apps back after they've closed, save the flock (see [Saved flocks](#saved-flocks)).

### Zones (FancyZones-style)

Hold **Shift** while dragging any window and the monitor's zones appear. Drop the window to snap it into
the zone under the cursor. Hold **Ctrl** as well to span several zones. Release Shift before dropping to
place the window normally. Settings → Zones can change the key to Alt, or show the zones on every drag.

The zones show only on the monitor you're dragging over. That's much cheaper on USB (DisplayLink) monitors;
Settings → Zones can show them on every monitor at once.

Each monitor has its own layout, picked in Settings → Zones or from edit mode: 2–4 columns, 2 or 3 rows,
2×2 / 2×3 / 3×3 grids, priority (25/50/25), main + side stack (either way round), half + 2×2, wide centre
+ corners, and focus (one centred window). Gaps between zones are on by default, with an adjustable size.

- **Zoned windows form a flock.** Every window dropped into a monitor's zones joins that monitor's zone
  flock, so the whole layout minimises and restores together. There's one zone flock per monitor per
  virtual desktop. Windows already in a flock you picked by hand stay in it.
- **Dragging a window out** of its zones takes it out of the zone flock and shrinks it back to the size it
  had before it was snapped, once it's dragged clear of its spot. Hold Shift before then to keep its zone
  size.
- **Resizing a zoned window by an inner edge** moves that split, and the windows on the other side follow
  live, like Windows 11 Snap Groups. Outer edges snap back on release.
- **Layouts follow the work area**, re-flowing as side panels and app bars dock and close.
- Layouts are remembered per physical monitor, so they stay put when Windows renumbers your screens after a
  wake or a redock.

### The snap strip

Drag a window to the top of a monitor and a strip of layouts drops down, with that monitor's current layout
first. Drop the window on a zone in any of them to snap it there; the monitor switches to that layout if
needed. Windows 11's own snap bar does the same job, so turn one of them off: Settings → Windows snapping
can switch Windows' off (and restart Explorer to apply it).

### Edit mode

Press `Alt+Shift+E` to put the layout on the monitor under the cursor into edit mode, with or without
windows in it. Press it again, press Esc, or switch to another app to finish.

- **Drag anywhere** on the outline to move the layout, and every window in it follows. Drop it on another
  monitor to move it there; if that monitor has a layout already, the two swap.
- **Double-click** the outline to make the layout fill the monitor again.
- **Ctrl+Z / Ctrl+Y** undo and redo layout changes.
- The **panel** at the top of the layout has:
  - undo, redo and a shortcut to Settings
  - the layout presets
  - **Splits:** **Adjust splits** shows a draggable strip over every split (double-click one to even
    them out), and **Even out** resets them
  - **Frame:** **Fill screen**
  - **Windows:** **Fill with open windows** drops the monitor's other visible windows into the free zones,
    **Minimise** puts the layout away, and **Release** sends each window back to the size and place it had
    before it was snapped
  - **Flock:** rename or save the layout's flock, or load a saved flock onto this monitor

### Saved flocks

Save a flock (Settings → Flocks, or the edit-mode panel) and Flamingle remembers its apps and where their
windows go. **Loading** it later finds each app's window if it's already open, starts the app if not, and
puts the windows back in place. When you have several monitors, loading from Settings asks which one to load
onto, and says what's in that monitor's zones now.

Editing a saved flock opens **design mode**: its layout drawn over your real screens, where you click a zone
to choose what starts there, with a floating panel for whatever's selected. Settings → Behaviour can switch
this to a form inside Settings instead. Each window has a launch recipe: a program with arguments, a Store
app, or "use a window that's already open". Saved flocks can declare inputs; for now every input takes its
default when the flock loads.

### Taskbar buttons

Each flock gets its own taskbar button, titled with its name. Clicking it minimises or restores the flock;
closing it dissolves the flock (the windows stay open).

- The icon is the Flamingle icon, numbered with the flock's monitor when there's more than one. Settings →
  Taskbar can switch it to a mosaic of the flock's app icons.
- Hovering the button previews the flock's windows laid out as they are on screen, with **Minimise /
  restore** and **Dissolve** buttons under the preview, and outlines the windows themselves (dashed for
  minimised ones).
- The button follows its flock to whichever virtual desktop most of its windows are on.

### Settings and data

Settings, live flocks, saved flocks and layouts are kept in `%APPDATA%\Flamingle` (tray → Open data folder).
Changes made in Settings apply straight away and are written to `settings.json`. You can also edit that file
by hand; restart Flamingle to pick up a hand edit.

Flamingle was called Gaggle until 2026-10-13. On first run it moves an existing `%APPDATA%\Gaggle` data
folder across to `%APPDATA%\Flamingle`.

## Building a release (maintainers)

1. Run `/bump-version` in Claude Code. It sets `<Version>` in `src/Flamingle.App/Flamingle.App.csproj` and
   writes that version's `CHANGELOG.md` section. Commit both.
2. Tag the commit and push it: `git tag v0.1.0`, then `git push origin main v0.1.0`.
3. [`.github/workflows/release.yml`](.github/workflows/release.yml) runs the tests, publishes a
   self-contained build, packs it with Velopack, and creates the GitHub Release with the setup, the portable
   zip, the update feed and `SHA256SUMS.txt`. It refuses to release if the csproj version or the changelog
   doesn't match the tag.

## Develop

```
run.bat                      # dev instance (green tray icon, data in %APPDATA%\Flamingle (Dev))
dotnet test flamingle.slnx
.\src\Flamingle.App\bin\Debug\net10.0-windows\flamingle.exe windows | Out-String   # list windows + snap-group guesses
.\tools\gen-icons.ps1        # after editing flamingle.svg: regenerate the .ico, tray frames and landing-icon.png
```

A dev instance runs beside an installed one, with its own data folder. Its Settings → Changelog page can
preview the "what's new" window for any version range.
