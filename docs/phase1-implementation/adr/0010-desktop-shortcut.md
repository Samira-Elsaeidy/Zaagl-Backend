# ADR-0010: Desktop shortcut default Ctrl+Alt+Z / Cmd+Ctrl+Z

Status: Accepted 2026-11-09 (PD-19, changed from the PRD). Addendum §14.

## Context
The PRD's Ctrl/Cmd+Shift+Z is Redo in most applications; a global hotkey would hijack it.

## Decision
Default Ctrl+Alt+Z on Windows and Cmd+Ctrl+Z on macOS, rebindable in settings; conflicts are reported at registration.

## Consequences
Differs from the PRD; marketing copy and onboarding must show the new default. Ctrl+Alt equals AltGr on some keyboard
layouts; the conflict handler must cover it.

## Rejected options
- PRD default with an onboarding warning: breaks Redo for every user by default.
