<h1 align="center">Flamingle</h1>

<p align="center">
 <img src="./landing-icon.png" width="150"  />
</p>

<p align="center">
<strong>Windows that flock together</strong>
</p>

<br>

Most work spans more than one window: an editor and a terminal, a browser and its dev tools, mail beside a
calendar. Windows treats each one as a stranger, so you minimise, restore and arrange them one at a time.
Flamingle is a small **Windows** tray app that groups windows into **flocks** that minimise, restore, move
and relaunch together, and snaps them into **layouts** on each monitor.

Already know you want it? [Skip the details and install it ▼](#installing)

### At a glance

- **Flocks**: pick a few windows, and one taskbar click puts them all away or brings them all back.
- **Zones**: hold Shift while dragging a window to snap it into a layout. Everything in a monitor's layout is
  a flock too.
- **Saved flocks**: save a flock, and later Flamingle starts its apps and puts every window back where it
  was.
- **A taskbar button per flock**, with a preview of the whole flock laid out as it is on screen.
- **Plays nicely with your setup**: layouts follow docked side panels, survive monitors being renumbered
  after a wake, and keep to one flock per virtual desktop.

Flamingle is young and still experimental. Most behaviours can be switched off in Settings if you'd rather
not have them.

## Installing

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/ArcticGizmo/flamingle/main/install.ps1 | iex
```

That's the whole install. No admin rights (it lands in `%LocalAppData%\Flamingle\`), a Start Menu shortcut
and a normal uninstaller in Settings → Apps, and Flamingle starts in the tray when it's done. Click the tray
icon to open Settings, then press `Alt+Shift+E` to edit the layout on the monitor under the cursor. Settings →
Behaviour can start Flamingle when you sign in.

What the script does, in order: finds the latest release, fetches `SHA256SUMS.txt` and
`Flamingle-win-Setup.exe`, **checks the installer against the checksums and deletes it rather than run it if
they don't match**, then runs the installer. It's [`install.ps1`](install.ps1) in this repo. Read it before
piping it into your shell, as you should with any installer.

Because PowerShell does the download rather than a browser, Windows doesn't mark the file as downloaded from
the internet. So this route never hits the **"Windows protected your PC"** SmartScreen warning.

Pin a version instead of taking the latest:

```powershell
$env:FLAMINGLE_VERSION = '0.1.0'; irm https://raw.githubusercontent.com/ArcticGizmo/flamingle/main/install.ps1 | iex
```

### Installing by hand

Rather click things? Download `Flamingle-win-Setup.exe` from the [latest release](../../releases/latest) and
run it. It's the same install.

A browser download *is* marked as coming from the internet, and the installer isn't code-signed yet, so
SmartScreen shows the blue **"Windows protected your PC"** dialog. Click **More info → Run anyway**, or use
the one-liner above and skip it. To check the download against the release's `SHA256SUMS.txt` yourself:

```powershell
$want = (Select-String -Path SHA256SUMS.txt -Pattern 'Flamingle-win-Setup.exe').Line.Split()[0]
(Get-FileHash Flamingle-win-Setup.exe -Algorithm SHA256).Hash -eq $want   # True
```

Prefer no installer at all? `Flamingle-win-Portable.zip` on the same release runs from wherever you extract
it.

## Updating

There's no in-app updater yet. To update, run the one-liner again (or the newer release's
`Flamingle-win-Setup.exe`). The first launch after an update shows what's new, and Settings → Changelog has
the full history. A portable copy updates by replacing its folder.

Your settings, flocks and layouts live in `%APPDATA%\Flamingle`, separate from the app, so updating,
uninstalling and reinstalling all keep them.

## What Flamingle does

Click the tray icon to open **Settings**, where everything lives. Right-click it for Settings, Open data
folder and Quit.

| To | Press |
|---|---|
| Put away / bring back flock *n* | `Alt+Shift+1` … `Alt+Shift+9` |
| Edit the layout on the monitor under the cursor | `Alt+Shift+E` (again, or Esc, to finish) |

Change them in Settings → Shortcuts: click one and press the new combination. To pick windows into a flock
by hand, use **New flock…** in Settings → Flocks.

### 1. Flocks: windows that minimise and restore together

- **One press, every window.** A press brings a buried flock forward first: if any of it is minimised, or
  covered by another window, it's restored and raised. A flock already in plain view minimises on the first
  press.
- **Linked minimise**: minimising or restoring any one window in a flock, from its title bar or the taskbar,
  takes the rest with it.
- **Back exactly where they were**: after a restore, Flamingle puts each window back in its place, for apps
  that have their own ideas.
- **Rename, save or dissolve** a flock in Settings → Flocks. Dissolving keeps the windows open.
- Flocks survive a Flamingle restart while their windows stay open.

### 2. Zones: snap windows into a layout

- **Hold Shift while dragging** a window and the monitor's zones appear. Drop it to snap it into the zone
  under the cursor; hold **Ctrl** as well to span several zones. (Settings → Zones can change the key to Alt,
  or show the zones on every drag.)
- **Each monitor has its own layout** (one per virtual desktop): 2–4 columns, 2 or 3 rows, 2×2 / 2×3 / 3×3 grids, priority
  (25/50/25), main + side stack, half + 2×2, wide centre + corners, or one centred window. Zones sit flush,
  with no gaps.
- **A layout is a flock.** Everything in a monitor's zones minimises and restores together, with one zone
  flock per monitor per virtual desktop.
- **Resize a zoned window by an inner edge** and the windows on the other side follow, like Windows 11 Snap
  Groups.
- **Drag a window out** and it shrinks back to its size from before it was snapped. Hold Shift before it
  shrinks to keep its zone size.
- **The snap strip**: drag a window to the top of a monitor and a strip of layouts drops down. Drop the
  window on any zone in any layout. Windows 11's own snap bar does the same job, so Settings → Windows
  snapping can turn that one off.
- Zones show only on the monitor you're dragging over, which keeps USB (DisplayLink) monitors smooth.
  Layouts follow side panels as they dock and close, and stay with their monitor when Windows renumbers your
  screens.

### 3. Edit mode: rearrange a layout

Press `Alt+Shift+E` and the layout on the monitor under the cursor gets an outline and a panel. It works with
or without windows in it.

- **Drag anywhere** on the outline to move the layout, windows and all. Drop it on another monitor to move it
  there; if that monitor already has a layout, the two swap.
- **Double-click** the outline to fill the monitor again. **Ctrl+Z / Ctrl+Y** undo and redo.
- **The panel** picks a layout, adjusts or evens out the splits, fills the zones with the monitor's other
  open windows, minimises the layout, or releases its windows back to where they were. Its **Flock** row
  renames or saves the layout's flock, or loads a saved one onto this monitor.

Press `Alt+Shift+E` again, press Esc, or switch to another app to finish.

### 4. Saved flocks: bring a whole setup back

- **Save** a flock from Settings → Flocks or the edit-mode panel. Flamingle remembers its apps and where each
  window goes.
- **Load** it later and Flamingle uses each app's window if it's already open, starts the app if not, and puts
  every window back in place. With several monitors it asks which one to load onto, and tells you what's in
  that monitor's zones now.
- **Design mode**: editing a saved flock draws its layout over your real screens. Click a zone to choose what
  starts there: a program with arguments, a Store app, or a window that's already open. (Settings →
  Behaviour switches this to a plain form.)

### 5. A taskbar button for every flock

- **Click it** to minimise or restore the flock. **Close it** to dissolve the flock; the windows stay open.
- **Hover it** for a preview of the flock's windows as they're laid out on screen, with Minimise / restore
  and Dissolve buttons, and an outline around each window (dashed for minimised ones).
- The icon is Flamingle's, numbered with the flock's monitor, or a mosaic of the flock's app icons (Settings →
  Taskbar). The button follows its flock to whichever virtual desktop most of it is on.

### Settings and data

Everything above is in the Settings window, and changes apply straight away. They're written to
`%APPDATA%\Flamingle\settings.json`, which you can also edit by hand (restart Flamingle to pick up a hand
edit). Tray → Open data folder takes you there.

Flamingle was called Gaggle until 2026-10-13. On first run it moves an existing `%APPDATA%\Gaggle` data
folder across.

## Building a release (maintainers)

Releases are created by pushing a version tag. GitHub Actions builds the installer and publishes it to the
GitHub Release.

1. Run `/bump-version` in Claude Code. It sets `<Version>` in `src/Flamingle.App/Flamingle.App.csproj` and
   writes that version's `CHANGELOG.md` section. Commit both.
2. Tag the commit and push it: `git tag v0.1.0`, then `git push origin main v0.1.0`.
3. [`release.yml`](.github/workflows/release.yml) runs the tests, publishes a self-contained build, packs it
   with Velopack, tests `install.ps1` against the packed installer, and creates the release with the setup,
   the portable zip, the update feed and `SHA256SUMS.txt`.

It refuses to release if the csproj version or the changelog doesn't match the tag, or if
`Flamingle-win-Setup.exe` is missing (the one-liner looks it up by name). `SHA256SUMS.txt` is generated in
the final job from the exact files being uploaded, so it can't drift from what users download.

Checksums prove the bytes you ran are the bytes that were published. They catch truncated downloads, a proxy
rewriting the payload, and tampering between GitHub and the disk. They aren't a signature: the checksums live
on the same release as the installer, so anyone who could replace one could replace both. Closing that gap
needs code signing.

After touching `install.ps1`, run `powershell -NoProfile -File tools\test-install.ps1`.

## Develop

```
run.bat                      # dev instance (green tray icon, data in %APPDATA%\Flamingle (Dev))
dotnet test flamingle.slnx
.\src\Flamingle.App\bin\Debug\net10.0-windows\flamingle.exe windows | Out-String   # list windows + snap-group guesses
.\tools\gen-icons.ps1        # after editing flamingle.svg: regenerate the .ico, tray frames and landing-icon.png
```

A dev instance runs beside an installed one, with its own data folder. Its Settings → Changelog page can
preview the "what's new" window for any version range.

See [docs/scope.md](docs/scope.md) for the plan.
