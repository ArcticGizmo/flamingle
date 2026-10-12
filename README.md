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
