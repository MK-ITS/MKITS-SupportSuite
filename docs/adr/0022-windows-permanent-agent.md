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
- **Session window in the user's session.** For each session the service starts the same EXE (`--agent-user <pipe>`)
  **as the user logged on at the console** (`WTSQueryUserToken`). It runs the chat, clipboard, file-transfer and shell
  handlers with the **user's** rights and shows the session window (status, supporters, security code, chat, *End
  session*; hidden under *silent*) and the consent dialog (*ask*). The service forwards only these channels' decrypted
  payloads over a named pipe (DACL SYSTEM + that user, network denied, single instance, client PID check; the window
  checks that the pipe server is the installed EXE) and accepts only those channels back – never video or input. The
  bridge protocol lives in the agent core (`UserSessionBridge` / `BridgeClient`), so the Linux agent can reuse it.
- **Fallback without a logged-on user:** `WTSSendMessage` (Yes/No under *ask*, 100 s – below the server's 2-minute
  timeout; information message under *notify*); with nobody at the console, *ask* requests are declined and the
  user-rights channels are unavailable.
- Upgrades: running `--install` again stops the service, replaces the EXE and keeps the identity (`--reenroll` forces
  a new enrollment). MSI packaging (Phase 10.2) wraps the same commands.

## Consequences

- Unattended view and control work at the logon screen, on the lock screen and with UAC prompts.
- Chat, files, clipboard and shell work like in ad-hoc sessions, with the logged-on user's rights (files land in the
  user's Downloads folder; the shell is the user's PowerShell/cmd, not SYSTEM). A SYSTEM shell for servers without a
  logged-on user is not offered.
- Malware running as the logged-on user could tamper with the session window (e.g. answer consent dialogs). This is the
  same boundary as for the ad-hoc client (ADR-0021); it cannot reach video, input, keys or other users' sessions.
- DPAPI machine scope does not protect against local administrators – by design they control the agent.
- Code signing (ADR-0009) is needed before production rollouts (SmartScreen, EDR reputation).
