# MKITS.SupportSuite.Agent.Linux

Linux agent (Debian 12+, Ubuntu 22.04+; x64 and arm64). ADR-0011: X11 and Wayland.

| Folder / type | Purpose |
|---|---|
| `Native/X11Native`, `TurboJpegNative`, `LibcNative` | P/Invoke: Xlib, XTest, XRandR, libjpeg-turbo, glibc PTY/spawn |
| `Platform/X11Display` | one X connection, serialized access, quiet X error handler (Xlib would otherwise exit the process) |
| `Platform/X11ScreenSource` | monitors from XRandR, `XGetImage` per monitor, JPEG via libjpeg-turbo |
| `Platform/X11InputInjector`, `KeyCodes` | XTest: pointer, buttons, wheel, keys by physical position (evdev code + 8), Unicode text via a temporarily mapped spare keycode |
| `Platform/PtyShellHost` | shells on a pseudo terminal (`posix_openpt`, `posix_spawn`, `setsid --ctty`) |
| `Terminal/` | terminal ad-hoc client (Phase 11A); the graphical client follows |

Runtime dependencies (Debian / Ubuntu package names): `libx11-6`, `libxtst6`, `libxrandr2`, `libturbojpeg0` / `libturbojpeg`, `util-linux` (`setsid`).

Try it on an X11 desktop: `mkits-agent --server https://support.example.com --code 12345678`.

Tests: `tests/MKITS.SupportSuite.Agent.Linux.Tests` start their own Xvfb (`sudo apt install xvfb libxtst6 libxrandr2 libturbojpeg`).
