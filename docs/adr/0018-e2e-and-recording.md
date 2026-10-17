# ADR-0018: End-to-end encryption and session recording

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

ADR-0015 asks for both end-to-end (E2E) encryption between agent and viewer and session recording. With E2E, the relaying server cannot see the content – so it cannot record it either. Recording by the viewer would be under the control of the supporter (who could skip it); recording by the agent is trustworthy but needs a destination.

## Options considered

1. **Server-side recording without E2E** – simple, but the server always sees everything.
2. **Viewer-side recording** – keeps E2E, but a supporter can avoid recording; browsers make background upload fragile.
3. **Agent-side recording, encrypted to a server recording key, only when recording is active** – E2E for non-recorded sessions; recorded sessions are readable by the operator, transparently announced.

## Decision

Option 3.

- E2E encryption is **always on** for the session plane (ECDH P-256, HKDF-SHA256, AES-256-GCM, SAS fingerprint; details in docs/protocol.md §6).
- If a session is recorded (group policy `Required`, or `Optional` and the supporter enables it), the **agent** additionally encrypts a copy of its outgoing video stream and the session's chat/file metadata with a per-recording key, which is itself encrypted with the server's **recording public key**, and uploads it to the server.
- Recording status is part of `SessionAssigned`, shown in the consent dialog and in the banner. A customer who does not accept recording can deny consent.
- Playback is restricted to Admins (and Users with an explicit permission, later) and audited.

## Consequences

- For recorded sessions the operator can read the content (by design and announced); for all other sessions the server cannot.
- Recordings contain the agent's view (screen + chat + file names), not the viewer's keystrokes as separate data.
- Additional upload bandwidth from the agent during recorded sessions.
