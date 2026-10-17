# ADR-0001: Graphical console sessions and remote shell

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

"Console sessions" in the requirements could mean the physical console desktop or a text terminal.

## Decision

Both are supported: graphical remote control of the **physical console session** (including login screen and UAC secure desktop; not RDP sessions) **and** a remote shell (ConPTY on Windows, PTY on Linux).

## Consequences

- Windows agent needs a privileged service that follows the active console session.
- The protocol needs separate channels for video/input and shell.
