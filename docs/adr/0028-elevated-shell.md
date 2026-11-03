# ADR-0028: Elevated remote shell

- **Status:** Accepted
- **Date:** 2026-11-03

## Context

Supporters need an administrator shell for most repair work (services, drivers, system files). The protocol has had
`ShellOpen.elevated` from the start, but every shell ran with the rights of the process hosting it: the ad-hoc client
(user rights), the Windows session window (user rights) and the Linux service (root, ADR-0025). On Windows the
privileged parts run elsewhere: the SYSTEM helper of the elevated ad-hoc mode (ADR-0021) and the agent service in
session 0 (ADR-0022). Terminal data is a continuous byte stream in both directions and must not be squeezed through
the helper's request/response pipe.

## Decision

- **Viewer:** a separate "Admin terminal" button opens a shell with `elevated = true`; the panel is marked. If the
  agent cannot elevate right now it answers with exit code -1 and the viewer says why.
- **Agent core:** `ShellHandler(host, elevatedHost)` – `elevated` selects the second host; without one the request is
  refused. The same rules as for normal shells apply (known shell types only, ≤ 3 per viewer, closed when the viewer
  leaves). `ElevatedShellRouter` splits the shell channel by shell id when elevated and normal shells live in
  different processes.
- **Windows ad-hoc client:** elevated shells exist only while administrator mode is on (the customer confirmed the UAC
  prompt). The SYSTEM helper starts the ConPTY shell (as SYSTEM, in the customer's session), keeps the pseudo console
  for resize/close and duplicates the input and output pipe ends and a wait-only process handle into the client
  process (`DuplicateHandle`) – terminal data then flows directly between client and shell. At most 6 such shells per
  helper.
- **Windows installed agent:** the service runs elevated shells itself (SYSTEM, ConPTY works in session 0); normal
  shells stay in the user's session window. `ElevatedShellRouter` keeps the two apart.
- **Linux installed agent:** shells already run as root (ADR-0025); elevated requests use the same host. The Linux
  ad-hoc client cannot elevate and refuses.
- **Permissions:** an elevated shell needs the same `shell` permission as a normal one; on permanent devices that
  permission already means administrative access (the agent runs as SYSTEM/root).

## Consequences

- "Administrator" means SYSTEM on Windows and root on Linux – more than a member of the Administrators group, and the
  same as what the agent itself can do. ScreenConnect's "backstage" works the same way.
- Code running in the ad-hoc client process could start a SYSTEM shell while administrator mode is on – but it could
  already drive the SYSTEM helper's input on the secure desktop; the trust boundary is unchanged (UAC consent).
- The helper keeps working on the request pipe while shells run; a broken helper ends its shells (pseudo consoles close
  with the helper).
