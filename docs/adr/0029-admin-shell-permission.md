# ADR-0029: A separate permission for administrator shells

- **Status:** Accepted
- **Date:** 2026-11-03
- **Supersedes:** the *Permissions* point of [ADR-0028](0028-elevated-shell.md)

## Context

ADR-0028 gave elevated shells (SYSTEM on Windows, root on Linux) to everybody with the device-group permission
*shell*. On the installed Windows agent that widens every existing *shell* grant from the logged-on user's rights to
SYSTEM; on Linux *shell* already meant root (ADR-0025). Granting a shell for log files should not silently grant full
control of the machine. The relay cannot tell the two apart: the shell channel is end-to-end encrypted, only the agent
sees `ShellOpen.elevated`.

## Decision

- New permission **`shell_admin`** (`ViewerPermissions.ShellAdmin` / `DevicePermissions.ShellAdmin` = 32, part of
  *All*; device-group checkbox *Admin shell (SYSTEM/root)*). Granting it also grants *shell* (the channel) and *view*.
  Admins and the owner of an ad-hoc session have all permissions, as before – ad-hoc elevation is still bounded by the
  customer's UAC consent (ADR-0021/0028).
- The relay keeps enforcing *shell* for the channel; the **agent** checks *shell_admin* when it opens an administrator
  shell (`ISessionContext.PermissionsOf`; unknown viewers have no permissions). A refused request gets exit code −1 and
  is never downgraded to a user shell.
- `ShellHandler(host, elevatedHost, hostIsAdmin)`: elevated requests always need the permission; `hostIsAdmin` marks a
  normal host that is itself root – the Linux service – so every shell there needs it.

## Consequences

- Existing *shell* grants keep user shells on Windows; on Linux servers they no longer open (root) shells until an
  administrator grants *admin shell* – a deliberate pre-release tightening of ADR-0025.
- One more checkbox in the group permissions; *admin shell* should be granted sparingly, it means full control.
