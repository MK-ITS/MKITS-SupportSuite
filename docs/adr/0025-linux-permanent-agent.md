# ADR-0025: Permanent Linux agent (systemd service + session window)

- **Status:** Accepted
- **Date:** 2026-10-25

## Context

Phase 11.5 needs an installed Linux agent for unattended access, analogous to the Windows service (ADR-0022). Linux
targets range from desktops with a graphical user session to headless servers where the remote shell is the main use.
There is no DPAPI, no "console session" API and no equivalent of the Windows SYSTEM helper; X servers require an
authorization cookie that belongs to the logged-on user.

## Decision

- **Packaging:** a `.deb` package (`mkits-agent`) installs the self-contained program in `/opt/mkits-agent`,
  `/usr/bin/mkits-agent` and the systemd unit `mkits-agent.service`. `sudo mkits-agent --install --server … --token …`
  writes the configuration and enables/starts the unit; `--uninstall` disables it and deletes configuration and
  identity; `apt purge` removes everything.
- **Service:** runs as root (`--agent-service`), holds the permanent connection, the device identity and the end-to-end
  keys (`DeviceAgent`, as on Windows). Exit code 78 marks problems a restart cannot fix (no configuration, rejected
  token): `RestartPreventExitStatus=78`, otherwise `Restart=on-failure`.
- **Secrets at rest:** no DPAPI equivalent; configuration (`/etc/mkits-agent`, token removed after enrollment) and
  identity (`/var/lib/mkits-agent`) are root-only by permissions (folders 0700, files 0600, `UMask=0077`) – the same
  protection as SSH host keys.
- **Screen and input:** the service connects to the X display of the active local graphical session itself. logind
  (`loginctl`) names the session; the session's processes supply `DISPLAY`/`XAUTHORITY`; the cookie comes from the X
  server's `-auth` file (Xorg command line) or the user's Xauthority and is passed with `XSetAuthorization` (no
  environment changes). Everything read from user processes is untrusted: only local displays, only files owned by
  root (when the X server runs as root) or by that user, checked on the opened file, never blocking on FIFOs. A lost X
  server (logout) must not end the service: `XSetIOErrorExitHandler` (libX11 ≥ 1.7) marks the display broken and the
  next access reconnects to the new session. Wayland sessions get no screen yet (11.2).
- **Session window:** chat, clipboard and files run in the user's session with the user's rights, through the existing
  `UserSessionBridge`. The service starts `mkits-agent --agent-user` with `runuser -u <user>` and a minimal environment
  (display, Xauthority, locale) and talks to it over its stdin/stdout – no socket another process could connect to.
  The window is hidden for the "silent" policy, as on Windows. Without a graphical session, consent requests are
  declined (nobody to ask) and these channels are not available.
- **Shell:** runs in the service, as **root** (Linux servers have no graphical session, and administration is the
  purpose; Windows runs it in the user's session). Access is controlled by the device-group permission "shell" and the
  consent policy; the relay and the agent enforce the permissions per channel (Phase 10).

## Consequences

- One program for the ad-hoc AppImage and the installed agent; servers without X work (shell only).
- Supporters with the "shell" permission on a Linux device get root. Administrators must grant that permission
  deliberately; it is documented in the admin guide.
- No sandboxing in the unit (`ProtectSystem` etc.): a remote administration tool needs the whole system.
- Auto-update (ADR-0023) uses the channel `linux-x64-deb`: the service verifies the signed package and installs it
  with `apt-get` in a transient systemd unit (`systemd-run`), outside its own control group – the package's postinst
  restarts the service, which would otherwise kill the installation. There is no automatic rollback (unlike Windows):
  a broken build is fixed by the next update or by installing a package manually.
- Display managers that start X without an `-auth` file and without a user Xauthority are not supported.
