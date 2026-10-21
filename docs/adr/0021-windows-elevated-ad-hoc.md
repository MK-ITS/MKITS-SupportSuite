# ADR-0021: Elevated ad-hoc mode on Windows (temporary service + SYSTEM helper)

- **Status:** Accepted
- **Date:** 2026-10-21

## Context

The portable ad-hoc client runs with the customer's user rights. Windows isolates the secure desktop (UAC prompts,
Ctrl+Alt+Del screen, lock screen) and elevated windows from such processes (UIPI), and Ctrl+Alt+Del can only be
raised by a LocalSystem service (`SendSAS`). Supporters need all of this. ADR-0001 requires access to the physical
console including the secure desktop; docs/architecture.md describes a service + SYSTEM helper process model.

## Decision

*Restart with administrator rights* in the client starts the same EXE through a **UAC prompt answered on the device**
(`--elevate`). The elevated copy

1. verifies that the requesting client process belongs to the account it claims,
2. copies the EXE into `%ProgramData%\MKITS-SupportSuite\adhoc\<id>\` (SYSTEM + Administrators only) with the
   session parameters (pipe name, client PID, user SID – no session secrets),
3. installs and starts a **temporary LocalSystem service** `MKITSSupport-<id>` (`--service`).

The service starts a **privileged helper** (`--helper`) as SYSTEM in the active console session. The helper follows
the input desktop (Default ↔ Winlogon) and performs capture and input injection; Ctrl+Alt+Del is forwarded to the
service (SYSTEM-only pipe), which calls `SendSAS`.

For ad-hoc sessions the **unprivileged client keeps the server connection, the UI and the end-to-end keys**; it only
forwards capture and input to the helper over a named pipe. The session continues without reconnecting, consent and
security code stay valid. (Permanent agents, Phase 10, run the connection in the service instead; the helper and its
IPC are shared.)

**Pipe protection:** DACL SYSTEM + the customer's SID, network access denied, single instance created with
`FirstPipeInstance`, exactly one connection whose process id must equal the recorded client PID; the client checks
that the server process is the EXE in the protected folder. All messages are size-limited.

**Lifetime:** the helper exits when the client disconnects (session end, client closed or crashed); the service then
deletes itself and its files (the running EXE at the next reboot). The service also stops if the client process is gone.

## Consequences

- The customer sees and answers the UAC prompt; nothing is elevated without it (security concept §6).
- While administrator mode is active, malware running **as the same user** could try to inject into the client process
  to use the helper. This is the same trust boundary as with comparable products; the mode is time-limited to the
  session, visible in the client window, announced in the chat, and requires the customer's own UAC approval.
- An elevated shell (SYSTEM / administrator) is not offered yet; the shell keeps the user's rights.
- Code signing (ADR-0009) is needed before production so that the UAC prompt shows a verified publisher.
