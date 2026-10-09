# ADR 0002: terminal control

Status: accepted (provisional). Date: 2026-10.

## Context
Phase 7 needs a terminal in the bottom dock for `exec`. The plan was to evaluate `XtermSharp.Avalonia`, `AvaloniaTerminal`/`SvcSystems.UI.Terminal` and `AgentSpace.Avalonia.XTerm` on Avalonia 12, AOT, resize, UTF-8, colors, copy/paste, mouse mode, license and maintenance.

## Decision
The candidate packages target Avalonia 11 or earlier and were not verified against Avalonia 12 / Native AOT on the three OSes in this iteration. Instead of blocking Phase 7, Kubism ships its own small engine:

* `TerminalBuffer` (pure, unit-tested, no UI): xterm-compatible subset (cursor movement, erase, scroll regions, insert/delete, SGR 16/256/true color, alternate screen, application cursor keys, bracketed paste, OSC titles, cursor position reports, UTF-8 with split chunks, scrollback).
* `TerminalControl`: renders the buffer, handles keys, resize (cols/rows from the control size), selection and copy/paste (⌘C/⌘V on macOS, Ctrl+Shift+C/V elsewhere).
* Both sit behind `ITerminalView`, so a full engine can replace them without touching `ExecSession` or the view models.

## Consequences
* Shells, `top`, `less` and simple full-screen apps work; mouse reporting, sixel and double-width CJK cells are not implemented.
* **Still to do (manual validation):** try `vim`/`htop` on each OS, then re-run the candidate evaluation when one supports Avalonia 12 + AOT.
