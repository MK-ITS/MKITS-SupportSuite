# Wire protocol

> Status: **specification (Phase 2)**, protocol version **0.1**. Normative schema: [`proto/mkits/v0/`](../proto/mkits/v0/).
> Versioning rules: [`ProtocolVersion`](../src/Common/MKITS.SupportSuite.Common/Protocol/ProtocolVersion.cs). Serialization decision: ADR-0017. Encryption: ADR-0018.

## 1. Transport

| Endpoint | Used by | Authentication |
|---|---|---|
| `wss://<host>/ws/agent` | agents (ad-hoc and permanent) | ad-hoc: session code; permanent: device key signature |
| `wss://<host>/ws/viewer` | browser and desktop viewers | short-lived viewer ticket (from the authenticated web session) |

- WebSocket over TLS, terminated at the reverse proxy. Sub-protocol header: `Sec-WebSocket-Protocol: mkits.v0`.
- **Binary messages only.** Each WebSocket message contains exactly one Protobuf `Frame`.
- Maximum message size: **1 MiB** (video frames are split by the sender if larger). Control messages: ≤ 64 KiB.
- Heartbeat: server sends `Ping` every **30 s**; a peer that does not answer within **90 s** is disconnected. WebSocket-level pings are additionally enabled for proxies.

## 2. Two planes

```
 Agent ◄──── control plane (plaintext to server, TLS-protected) ────► Server ◄──── control plane ────► Viewer
   │                                                                                                   │
   └──────────── session plane: SessionData frames, E2E-encrypted, routed opaquely by the server ───────┘
```

- **Control plane** – messages between a peer and the server: handshake, authentication, session management, consent, presence, errors. The server reads them.
- **Session plane** – `SessionData` frames between agent and viewer(s). Payload is AES-256-GCM ciphertext; the server only reads the routing header (`session_id`, `channel`, `sequence`).

## 3. Handshake

```
peer                                   server
 │── Hello{protocol, role, platform, ──►│
 │        version, capabilities}        │   incompatible major → Error{PROTOCOL_MISMATCH} + close
 │◄── HelloAck{server_version, ─────────│
 │        nonce, heartbeat_seconds}     │
 │── Authenticate{...} ────────────────►│   failures count for the IP ban service
 │◄── AuthResult{ok, peer_id} ──────────│
```

`Authenticate` variants:

| Variant | Peer | Content |
|---|---|---|
| `AdHocCode` | ad-hoc agent | session code, machine name, OS info |
| `DeviceSignature` | permanent agent | device id, ECDSA P-256 signature over `HelloAck.nonce` |
| `Enrollment` | new permanent agent | enrollment token, device public key, machine info |
| `ViewerTicket` | viewer | one-time ticket issued by `POST /api/sessions/{id}/viewer-ticket` |

## 4. Control messages (excerpt)

| Message | Direction | Purpose |
|---|---|---|
| `SessionAssigned` | server → agent | agent bound to a session; includes consent policy and whether recording is active |
| `ViewerJoined` / `ViewerLeft` | server → agent | supporter (display name, role) joined/left |
| `ConsentRequest` | server → agent | ask the end user; agent answers `ConsentResponse{granted}` |
| `SessionState` | server → peers | state changes (see [session-lifecycle.md](session-lifecycle.md)) |
| `EndSession` | any → server | end with reason |
| `KeyExchange` | agent ↔ viewer (relayed) | ephemeral ECDH public keys (see §6) – relayed as control because it precedes encryption |
| `Ping` / `Pong` | both | heartbeat |
| `Error` | server → peer | code + message key (localized by the client) |

## 5. Session plane channels

| Id | Channel | Direction | Content |
|---|---|---|---|
| 1 | `VIDEO` | agent → viewer | `VideoFrame{monitor_id, codec, keyframe, timestamp_us, width, height, data}` – H.264 Annex B, fallback JPEG |
| 2 | `INPUT` | viewer → agent | `PointerEvent{monitor_id, x, y (0..65535), buttons, wheel}`, `KeyEvent{code, key, down, modifiers}`, `SpecialKey{CTRL_ALT_DEL, WIN, …}` |
| 3 | `DISPLAY` | both | `MonitorList`, `SelectMonitor`, `QualitySettings`, `FrameAck` (flow control) |
| 4 | `CLIPBOARD` | both | `ClipboardText` (UTF-8, ≤ 1 MiB); files via channel 5 |
| 5 | `FILE` | both | `FileOffer`, `FileAccept`/`FileReject`, `FileChunk` (64 KiB), `FileComplete{sha256}`, `FileCancel` |
| 6 | `CHAT` | both | `ChatMessage{author, text, timestamp}` |
| 7 | `SHELL` | both | `ShellOpen{type, cols, rows, elevated}`, `ShellData`, `ShellResize`, `ShellExit{code}` |

**Flow control (video):** the viewer acknowledges every frame with `FrameAck{sequence}`. The agent keeps at most **N = 3** unacknowledged frames in flight and skips capture otherwise; after a skip it sends the next frame as a keyframe if the gap exceeded 1 s. The relay additionally drops video frames (never other channels) for a viewer whose send buffer exceeds 4 MiB.

**Input coordinates** are normalized to `0..65535` per monitor, so viewer scaling never needs agent-side knowledge.

**Keyboard:** `code` is the physical key (W3C `KeyboardEvent.code`, e.g. `KeyA`), `key` the produced character. The agent injects scan codes for `code` by default; text mode (unicode injection of `key`) is used when layouts differ.

## 6. End-to-end encryption

1. After the viewer joins, agent and viewer each generate an **ephemeral ECDH P-256** key pair and exchange public keys via `KeyExchange` (relayed by the server).
2. Both derive `shared = ECDH(own_private, peer_public)` and run **HKDF-SHA256** with `salt = session_id` and `info = "mkits/v0/e2e" || agent_pub || viewer_pub` to obtain two 256-bit keys: `key_agent_to_viewer`, `key_viewer_to_agent`.
3. Each `SessionData` payload is encrypted with **AES-256-GCM**. Nonce (96 bit) = `channel (32 bit) || sequence (64 bit)`; sequence numbers are per direction and channel and must strictly increase (replay protection). Associated data = `session_id || channel || sequence`.
4. **Fingerprint (SAS):** both sides display a 6-digit code derived from `SHA-256(transcript)`. Supporter and customer can compare it by phone to rule out a man-in-the-middle by a compromised server.
5. **Permanent agents** additionally sign their ephemeral key with their long-term device key; the viewer verifies it against the device key registered at enrollment.
6. Rekeying: a new key exchange is performed every 2^32 frames per direction or 24 h, whichever comes first.

P-256 is chosen over X25519 because it is available in WebCrypto in all supported browsers, in .NET on every platform and on Android 10+.

## 7. Errors

`Error.code` values (stable, English identifiers; UI text via localization): `PROTOCOL_MISMATCH`, `AUTH_FAILED`, `IP_BLOCKED`, `CODE_INVALID`, `CODE_EXPIRED`, `SESSION_ENDED`, `CONSENT_DENIED`, `NOT_AUTHORIZED`, `RATE_LIMITED`, `MESSAGE_TOO_LARGE`, `INTERNAL`.

## 8. Compatibility rules

- Unknown fields are ignored (Protobuf default) – allows adding fields in minor versions.
- Unknown message types and channels are ignored and logged at debug level.
- Peers announce optional features in `Hello.capabilities` (e.g. `video.h264`, `video.jpeg`, `shell.conpty`, `clipboard.files`); features are only used if both sides announce them.
