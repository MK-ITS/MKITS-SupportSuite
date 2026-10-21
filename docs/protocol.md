# Wire protocol

> Status: **implemented on the server (Phase 5, permanent devices Phase 10)**, protocol version **0.3**. Normative schema: [`proto/mkits/v0/`](../proto/mkits/v0/).
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
| `ViewerTicket` | viewer | one-time ticket (60 s) issued by the server to an authorized supporter's web session |
| `Resume` | agent after a connection loss | `session_id` + the one-time `AuthResult.resume_token` of its previous connection (while the session is *Suspended*, or while the server still holds the old connection – that one is then dropped) |

`AuthResult.resume_token` (agents only) is replaced on every successful (re)connect; the server stores only its hash.

**Permanent devices** (Phase 10):

1. *Enrollment* – the agent creates an ECDSA P-256 key pair (the private key never leaves the device) and sends `Enrollment{token, device_public_key, machine}`. On success `AuthResult.peer_id` is the new **device id** and the server closes the connection. Invalid tokens count for the IP ban service.
2. *Authentication* – on every connection the agent signs `HelloAck.nonce` (ECDSA P-256 / SHA-256, IEEE P1363 `r‖s`) and sends `DeviceSignature{device_id, signature}`. Devices awaiting approval get `NOT_AUTHORIZED`, disabled or unknown devices `AUTH_FAILED`. Bad signatures of known devices count for the IP ban service; unknown device ids do not (a removed device that keeps retrying must not ban the customer's network).
3. *Presence* – after `AuthResult` the connection stays open and idle (heartbeat only). When a supporter opens the device, the server sends `SessionAssigned{session_id, consent_policy, operator_name}` over this connection; after `SessionState{ENDED}` the device returns to idle. A newer connection of the same device replaces the older one.
4. *Connection loss during a session* – the session is *Suspended*; when the device authenticates again within the grace period it is bound to the session again (`SessionAssigned` with the same id, viewers re-key). No resume token is needed: the signature proves it is the same device.
Handshake timeouts: `Hello` within 10 s after the upgrade, `Authenticate` within 30 s after `HelloAck`.

## 4. Control messages (excerpt)

| Message | Direction | Purpose |
|---|---|---|
| `SessionAssigned` | server → agent | agent bound to a session; includes consent policy and whether recording is active |
| `ViewerJoined` / `ViewerLeft` | server → agent | supporter (display name, role) joined/left |
| `ConsentRequest` | server → agent | ask the end user for one viewer (`viewer_id`); agent answers `ConsentResponse{viewer_id, granted}` |
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
| 5 | `FILE` | both | `FileOffer`, `FileDecision{accepted}`, `FileChunk` (64 KiB), `FileComplete{sha256}`, `FileCancel` |
| 6 | `CHAT` | both | `ChatMessage{author, text, timestamp}` |
| 7 | `SHELL` | both | `ShellOpen{type, cols, rows, elevated}`, `ShellData`, `ShellResize`, `ShellExit{code}` |

**Routing rules (enforced by the relay):**

- Viewers can send `SessionData` and `KeyExchange` only after the customer granted consent for them; earlier frames are dropped.
- The relay overwrites `viewer_id` on frames from viewers with the sender's real id (no spoofing).
- `SessionData` from the agent with an empty `viewer_id` goes to all granted viewers, otherwise to that viewer only.
- Channel `INPUT` is accepted only from the controlling viewer (the first granted viewer; explicit hand-over follows with the viewer UI).
- At most 5 viewers per session.
- **Permissions** (permanent devices, per device group): a viewer may only send *and* receive on channels it has the permission for – `VIDEO`/`DISPLAY`/`CHAT`: *view*, `INPUT`: *control*, `CLIPBOARD`: *clipboard*, `FILE`: *files*, `SHELL`: *shell*; unknown channels need all permissions. The relay only needs the plaintext channel header for this; the agent rejects payloads that do not match their channel, so a viewer cannot smuggle a payload through another channel. The controlling viewer is the first granted viewer with *control*. Ad-hoc sessions grant all permissions to the owner and Admins. `ViewerJoined.permissions` and `ConsentRequest.requested_permissions` tell the agent what a viewer may do.
- **Consent policy** (`SessionAssigned.consent_policy`): *ask* → `ConsentRequest` per viewer; *notify* → no question, the agent shows a visible banner; *silent* → no question, no banner. Ad-hoc sessions always ask.

**Flow control (video):** the viewer acknowledges every frame with `FrameAck{sequence}`. The agent keeps at most **N = 3** unacknowledged frames in flight and skips capture otherwise; after a skip it sends the next frame as a keyframe if the gap exceeded 1 s. The relay additionally drops video frames (never other channels) for a peer whose send queue exceeds 4 MiB, and disconnects a peer whose queue exceeds 16 MiB (it cannot keep up).

**Input coordinates** are normalized to `0..65535` per monitor, so viewer scaling never needs agent-side knowledge.

**Keyboard:** `code` is the physical key (W3C `KeyboardEvent.code`, e.g. `KeyA`), `key` the produced character. The agent injects scan codes for `code` by default; text mode (unicode injection of `key`) is used when layouts differ.

## 6. End-to-end encryption

> Reference implementation: [`Common/Crypto/SessionCrypto.cs`](../src/Common/MKITS.SupportSuite.Common/Crypto/SessionCrypto.cs) (used by agents and the desktop viewer; the browser viewer mirrors it with WebCrypto). The shared secret is the raw ECDH x-coordinate (= WebCrypto `deriveBits`). In the HKDF `info` and the security-code input, concatenations (`||`) are length-prefixed (4-byte big-endian length before each part). The AES-GCM nonce (`channel` u32 BE ‖ `sequence` u64 BE, 12 bytes) and associated data (UTF-8 `session_id` ‖ `channel` u32 BE ‖ `sequence` u64 BE) are plain fixed-width concatenations without prefixes. The security code is `uint32_be(SHA-256("mkits/v0/sas" || session_id || agent_pub || viewer_pub)[0..4]) mod 10^6`, shown as `123 456`.

1. After consent was granted, the **viewer** generates an **ephemeral ECDH P-256** key pair and sends its public key in `KeyExchange`; the agent answers with its own ephemeral key (relayed by the server, which stamps `viewer_id`). A new `KeyExchange` from the viewer replaces the keys (rekeying, reconnect). Viewers start a new key exchange on **every** `SessionState{ACTIVE}` they receive; the server sends it when consent is granted and again after the agent resumed (the agent loses its keys with the connection).
2. Both derive `shared = ECDH(own_private, peer_public)` and run **HKDF-SHA256** with `salt = session_id` and `info = "mkits/v0/e2e" || agent_pub || viewer_pub` to obtain two 256-bit keys: `key_agent_to_viewer`, `key_viewer_to_agent`.
3. Each `SessionData` payload is encrypted with **AES-256-GCM**. Nonce (96 bit) = `channel (32 bit) || sequence (64 bit)`; sequence numbers are per direction and channel and must strictly increase (replay protection). Associated data = `session_id || channel || sequence`.
4. **Fingerprint (SAS):** both sides display a 6-digit code derived from `SHA-256(transcript)`. Supporter and customer can compare it by phone to rule out a man-in-the-middle by a compromised server.
5. **Permanent agents** additionally sign their ephemeral key with their long-term device key (`KeyExchange.device_signature`, ECDSA P-256 / SHA-256, IEEE P1363); the viewer verifies it against the device key registered at enrollment (the viewer page embeds it as `data-device-key`) and refuses the session plane with `DEVICE_SIGNATURE_INVALID` otherwise. This binds the E2E keys to the enrolled device – a relay that swaps keys is detected as long as the server that stored the device key was not compromised at enrollment time.
6. Rekeying: a new key exchange is performed every 2^32 frames per direction or 24 h, whichever comes first.

P-256 is chosen over X25519 because it is available in WebCrypto in all supported browsers, in .NET on every platform and on Android 10+.

## 7. Errors

`Error.code` values (stable, English identifiers; UI text via localization): `PROTOCOL_MISMATCH`, `AUTH_FAILED`, `IP_BLOCKED`, `CODE_INVALID`, `CODE_EXPIRED`, `SESSION_ENDED`, `CONSENT_DENIED`, `NOT_AUTHORIZED`, `RATE_LIMITED`, `MESSAGE_TOO_LARGE`, `INTERNAL`.

## 8. Compatibility rules

- Unknown fields are ignored (Protobuf default) – allows adding fields in minor versions.
- Unknown message types and channels are ignored and logged at debug level.
- Version history: **0.1** initial schema; **0.2** adds `Authenticate.resume`, `AuthResult.resume_token`, `ConsentRequest.viewer_id`, `ConsentResponse.viewer_id`; **0.3** adds `ViewerJoined.permissions` and implements enrollment, device signatures and presence on the server.
- Peers announce optional features in `Hello.capabilities` (e.g. `video.h264`, `video.jpeg`, `shell.conpty`, `clipboard.files`); features are only used if both sides announce them.
