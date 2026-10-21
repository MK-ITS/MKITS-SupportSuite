# ADR-0022: Permanent Windows agent (service + on-demand helper)

- **Status:** Accepted
- **Date:** 2026-10-21

## Context

Permanent devices (ADR-0013, ADR-0014) need unattended access: the agent must connect without anybody logged on,
survive reboots, and reach the secure desktop. Services run in session 0 and cannot see or control the interactive
desktop. The elevated ad-hoc mode (ADR-0021) already has a SYSTEM helper that captures and injects input on the input
desktop of the console session, controlled over a protected named pipe.

## Decision

- **One binary.** The Windows client EXE is also the agent: `--install --server <url> --token <token>` (Administrator)
  copies it to `%ProgramFiles%\MKITS-SupportSuite\Agent\MKITS-Agent.exe` and registers the service **`MKITSAgent`**
  (LocalSystem, automatic start, restart on failure); `--uninstall` removes service, program and data.
- **The service holds the connection**, the device identity and the end-to-end keys (`DeviceAgent`, agent core).
  The device key and the enrollment token are DPAPI-protected (machine scope, purpose-specific entropy) and stored in
  `%ProgramData%\MKITS-SupportSuite\agent\`, which only SYSTEM and Administrators can access. The token is removed from
  the configuration after enrollment.
- **On-demand helper.** Screen and input go through the ADR-0021 helper (`--agent-helper`), started as SYSTEM in the
  active console session **only while a session needs it** and stopped when the session ends; it is replaced when the
  console session changes. Pipe DACL SYSTEM only; the helper accepts only the service's process id, and the service
  checks that the pipe server is the installed EXE. Ctrl+Alt+Del is raised by the service (`SendSAS`).
- **User interaction without an extra process (first version):** `WTSSendMessage` shows a Yes/No message in the
  console session under the *ask* policy (100 s, below the server's 2-minute timeout) and an information message under
  *notify*; *silent* shows nothing. If nobody is logged on at the console, *ask* requests are declined.
- Upgrades: running `--install` again stops the service, replaces the EXE and keeps the identity (`--reenroll` forces
  a new enrollment). MSI packaging (Phase 10.2) wraps the same commands.

## Consequences

- Unattended view and control work at the logon screen, on the lock screen and with UAC prompts.
- Chat, file transfer, clipboard and the shell are **not** offered by the service yet: they need a component running in
  the logged-on user's session (with that user's rights and clipboard), planned next. Until then these channels are
  simply not handled (and permissions keep them closed on the relay anyway).
- `WTSSendMessage` boxes are plain; a branded consent window and a permanent banner come with the user-session component.
- DPAPI machine scope does not protect against local administrators – by design they control the agent.
- Code signing (ADR-0009) is needed before production rollouts (SmartScreen, EDR reputation).
