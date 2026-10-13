# Flamingle — project conventions

See `docs/scope.md` for what we're building and the decisions so far.

## Experimental first

The feel is still being proven out. Prefer a quick, honest spike over polish. Put behaviours we're
unsure about behind a toggle (tray menu or `settings.json`) so they can be compared side by side.

## Layout

- `src/Flamingle.Core` — pure logic (grouping, snap-group heuristic, toggle rules, persistence). No Win32,
  no Avalonia. Everything here gets an xunit test in `tests/Flamingle.Tests`.
- `src/Flamingle.App` — Avalonia tray app + hand-rolled P/Invoke in `Native/`. Keep Win32 calls there.

## Win32 rules of thumb

- Act on other processes' windows with `ShowWindowAsync` / `SWP_ASYNCWINDOWPOS` — a hung app must never
  block our UI thread.
- Every change Flamingle makes to a member window must go through the `EchoSuppressor`, otherwise the
  WinEvent hooks see our own change and loop.

## Dev build

Always stop the running Flamingle before rebuilding, so the user never tests a stale binary
(`Get-Process flamingle | Stop-Process -Force`, then build, then relaunch the exe).
