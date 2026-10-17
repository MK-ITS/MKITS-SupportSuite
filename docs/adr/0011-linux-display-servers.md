# ADR-0011: Linux agent supports X11 and Wayland from the start

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Ubuntu and Debian default to Wayland on GNOME; many systems still use X11.

## Decision

The first Linux release supports **X11** (XShm/XDamage, XTest) **and Wayland** (xdg-desktop-portal ScreenCast/RemoteDesktop, PipeWire) for GNOME and KDE.

## Consequences

- Significantly more Linux effort in M3.
- Unattended Wayland access depends on the desktop environment; limitations are documented.
