# ADR-0026: Wayland through xdg-desktop-portal and PipeWire

- **Status:** Accepted
- **Date:** 2026-10-27

## Context

ADR-0011 requires Wayland support. Wayland compositors do not let clients read the screen or inject input; the
standard way for remote desktop tools is xdg-desktop-portal (`RemoteDesktop` + `ScreenCast`), implemented by GNOME
(xdg-desktop-portal-gnome / mutter) and KDE (xdg-desktop-portal-kde / KWin). Frames arrive as a PipeWire video
stream, input goes through portal D-Bus calls. The desktop always asks the user in its own dialog.

## Decision

- **D-Bus:** Tmds.DBus.Protocol 0.21.3 (MIT) – the version Avalonia already ships, so there is one copy. The client
  subscribes to the `Response` signal at the predictable request path before each call (portal ≥ 0.9).
- **Session:** one RemoteDesktop session with keyboard + pointer, `ScreenCast.SelectSources` for all monitors
  (multiple, cursor embedded), `Start`, then one `OpenPipeWireRemote` per stream. Ad-hoc sessions never request
  persistence – the dialog is the user's consent to sharing, asked every time.
- **Frames:** libpipewire-0.3 via P/Invoke (no GStreamer dependency). The EnumFormat POD is built by hand (the libspa
  builder is header-only) and offers raw 32-bit RGB only, without DMA-BUF modifiers, so compositors send shared
  memory; the latest frame is copied on PipeWire's thread and JPEG-encoded on request, as with X11.
- **Input:** portal `NotifyPointerMotionAbsolute` (stream-relative logical coordinates), `NotifyPointerButton`
  (evdev button codes), `NotifyPointerAxisDiscrete`, `NotifyKeyboardKeycode` (evdev, same mapping as X11) and
  `NotifyKeyboardKeysym` for text; sent without waiting for replies, in order.
- **Scope of this step:** the ad-hoc client (customer present). The installed agent on Wayland (needs the portal in
  the user's session process and a restore token for unattended access) and the login screen follow separately.

## Consequences

- GNOME and KDE on Wayland work for ad-hoc support; the user picks the screens and can stop sharing at any time from
  the desktop's indicator. Compositors without RemoteDesktop portal (e.g. some wlroots setups) are reported clearly.
- Unattended access on Wayland remains limited by the desktops (GNOME: restore tokens; KDE: permissions dialog).
- Tests: the D-Bus handshake runs against a fake portal on a private session bus; frames come from a private PipeWire
  daemon with a GStreamer test source. A real compositor is not part of CI.
