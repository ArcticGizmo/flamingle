# Saved flock design mode

Editing a saved flock by pointing at the real screens instead of filling in a form. Behind
`DesignModeEditing` (Settings → Behaviour → *Design saved flocks on screen*, on by default); off, Edit opens the
form in Settings → Flocks as before. The form stays for now, with the intention that design mode replaces it.

## How it works

- **Surfaces** (`Views/Design/DesignSurfaceWindow`): one per monitor, over its work area, dimmed. The flock's layout
  on that monitor is drawn as tiles — one per region (a set of zones some windows go into), plus one per empty zone.
  Windows that go to that monitor but not into a zone of its layout sit on a "Not in a zone" shelf. A chip at the top
  names the monitor and its layout. Topmost, take the mouse, never activation.
- **Panel** (`Views/Design/DesignPanelWindow`): floats beside whatever is selected; hosts `SavedFlockEditor` with a
  *focus* (`EditorFocus`): the flock (inputs, Test load), a window (where it goes + how it starts), "what starts
  here?" for an empty zone, or a monitor's layout picker. Activatable (it takes typing); Esc deselects, then leaves.
- **Service** (`Services/FlockDesignService`): owns selection, redraws on every draft change, positions the panel,
  handles drags and drag-in.
- **Core** (`Core/Saved/FlockDesign`): regions per screen, layout switching (zones mapped by centre), placing,
  adding, replacing and removing windows; apps picked rather than captured (`ForProgram`, `ForStoreApp`).

## What you can do

- Click an empty zone → choose what starts there: an **open window** (captured as saving a flock does), an
  **installed app** (Start menu's Apps folder: desktop shortcuts by exe, Store apps by AUMID —
  `Native/InstalledApps`), **Browse** for an exe, or **drag a real window** onto a zone (the screens go
  click-through until the drop; the live zone drag is paused meanwhile).
- Click a window → its zone map (click a zone to move it, Ctrl+click to span), Move to another monitor, Change app,
  and the usual recipe editing (arguments, start in, more options, undo, remove).
- Drag a window's tile onto another zone (Ctrl on drop spans from its first zone).
- Click a monitor's chip → pick another layout for it; its windows move to the zones under their old centres. Only
  the saved flock changes, not the live layout.

## Not yet / open questions

- Changing the monitor set mid-session isn't followed (close and reopen).
- Free (non-zoned) windows can't be resized or moved freely — only put into a zone.
- No app icons on tiles yet (initials).
- Store apps picked from the list have no captured class/title, so matching is by AUMID alone.
- Whether the panel should follow the selection or stay where it was dragged.
