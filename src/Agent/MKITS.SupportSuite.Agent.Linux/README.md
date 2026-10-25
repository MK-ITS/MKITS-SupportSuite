# MKITS.SupportSuite.Agent.Linux

Linux agent (Debian 12+, Ubuntu 22.04+; x64 and arm64). ADR-0011: X11 and Wayland.

| Folder / type | Purpose |
|---|---|
| `Native/X11Native`, `TurboJpegNative`, `LibcNative` | P/Invoke: Xlib, XTest, XRandR, libjpeg-turbo, glibc PTY/spawn |
| `Platform/X11Display` | one X connection, serialized access, quiet X error handler (Xlib would otherwise exit the process) |
| `Platform/X11ScreenSource` | monitors from XRandR, `XGetImage` per monitor, JPEG via libjpeg-turbo |
| `Platform/X11InputInjector`, `KeyCodes` | XTest: pointer, buttons, wheel, keys by physical position (evdev code + 8), Unicode text via a temporarily mapped spare keycode |
| `Platform/PtyShellHost` | shells on a pseudo terminal (`posix_openpt`, `posix_spawn`, `setsid --ctty`) |
| `Gui/` | graphical ad-hoc client (Avalonia, code-only UI): session window, consent and file dialogs, connect window |
| `Terminal/` | terminal ad-hoc client (`--terminal`, or automatically without a graphical desktop) |
| `Permanent/` | installed agent (ADR-0025): `--install`/`--uninstall`, systemd service (`AgentService`), X display of the logged-on user (`DesktopLocator`, `XAuthority`, `SessionDesktop`), session window in the user's session (`UserSessionConnector`, `UserProcess`), root shell |
| `../../../deploy/linux/build-appimage.sh` | AppImage (self-contained folder + bundled libjpeg-turbo); also built into the server image |
| `../../../deploy/linux/build-deb.sh`, `mkits-agent.service`, `deb/` | `.deb` package of the installed agent with the systemd unit |

Runtime dependencies (Debian / Ubuntu package names): `libx11-6`, `libxtst6`, `libxrandr2`, `libturbojpeg0` / `libturbojpeg`, `util-linux` (`setsid`, `runuser`).

Installed agent: `sudo apt install ./mkits-agent_<version>_amd64.deb`, then `sudo mkits-agent --install --server https://support.example.com --token <enrollment token>` (`--reenroll` for a new identity, `--uninstall` to disconnect). Log: `journalctl -u mkits-agent`. Configuration in `/etc/mkits-agent`, identity in `/var/lib/mkits-agent` (root only). The remote shell of the installed agent runs as root.

Try it on an X11 desktop: `mkits-agent --server https://support.example.com --code 12345678` (add `--terminal` for the terminal version). Customers get `MKITS-Support_<host>_<code>.AppImage` from the portal; on systems without FUSE run it with `--appimage-extract-and-run`. Wayland sessions are not supported yet (11.2): the client says so.

Tests: `tests/MKITS.SupportSuite.Agent.Linux.Tests` start their own Xvfb (`sudo apt install xvfb libxtst6 libxrandr2 libturbojpeg`).
