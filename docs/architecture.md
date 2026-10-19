# Architecture

> Status: **specification (Phase 2)**. Describes the target architecture. Parts marked *(planned)* are not implemented yet.
> Decisions referenced as ADR-NNNN live in [adr/](adr/README.md).

## 1. System context

```
 ┌───────────────┐        ┌──────────────────────────────────────────────┐        ┌──────────────────┐
 │ End device    │        │ Operator infrastructure                       │        │ Supporter        │
 │ (customer)    │        │                                               │        │                  │
 │  Agent ───────┼─WSS───►│ nginx :443 ──► MKITS Server :8080 (Docker)    │◄─WSS───┼── Browser viewer │
 │               │        │  TLS,          ├─ Web UI / REST API           │        │   or desktop     │
 │  Browser ─────┼─HTTPS─►│  WebSocket     ├─ Session broker + relay      │◄─HTTPS─┼── viewer         │
 │  (portal)     │        │  upgrade       └─ SQLite + files on /data     │        │                  │
 └───────────────┘        └──────────────────────────────────────────────┘        └──────────────────┘
```

- **Only outbound connections** from agents and viewers, all on TCP 443 (ADR-0005). End devices never accept inbound connections.
- **The server relays** session traffic. Session payloads are end-to-end encrypted between agent and viewer (ADR-0018); the server sees metadata (who, when, how much), not content – except for sessions that are explicitly recorded.

## 2. Components

| Component | Project | Responsibility |
|---|---|---|
| Server | `src/Server/MKITS.SupportSuite.Server` | Web UI, REST API, authentication, session broker, relay, device registry, IP bans, audit log, recordings, agent downloads and updates |
| Common | `src/Common/MKITS.SupportSuite.Common` | Product info, protocol definitions (generated from `proto/`), shared value types |
| Agent core | `src/Agent/MKITS.SupportSuite.Agent.Core` | Connection, protocol, E2E crypto, session state machine, channel handlers, platform abstractions |
| Windows agent | `src/Agent/MKITS.SupportSuite.Agent.Windows` | DXGI capture, Media Foundation H.264, SendInput, service + session helper, ConPTY |
| Linux agent | `src/Agent/MKITS.SupportSuite.Agent.Linux` | X11 + Wayland capture/input, PTY, systemd integration |
| Android agent | `android/` | Kotlin app: MediaProjection, MediaCodec, AccessibilityService |
| Browser viewer | `src/Viewer/web` | TypeScript: canvas + WebCodecs, input, WebCrypto, xterm.js |
| Desktop viewer | `src/Viewer/desktop` | Avalonia app *(planned)* |

## 3. Server internals

The server is a single ASP.NET Core process, organized **by feature** (see [coding-guidelines.md](coding-guidelines.md)):

```
Server
├── Infrastructure/         cross-cutting: configuration, reverse proxy, localization, security headers, data access
├── Features/
│   ├── Identity/           users, roles, login, TOTP, first-run setup              (Phase 4)
│   ├── IpBans/             failure tracking, bans, allow/deny lists, notifications (Phase 4)
│   ├── Audit/              append-only, hash-chained audit log                     (Phase 4)
│   ├── Sessions/           session codes, lifecycle, consent                       (Phase 5)
│   ├── Relay/              WebSocket endpoints, routing, backpressure              (Phase 5)
│   ├── Devices/            groups, permissions, enrollment, device registry        (Phase 10)
│   ├── Downloads/          agent binaries, ad-hoc file naming, updates             (Phase 6/10)
│   └── Recordings/         storage, retention, playback                            (Phase 13)
├── Endpoints/              system endpoints (health, version)
└── Resources/              localized UI strings (en, de)
```

Rules:
- Features talk to each other through interfaces registered in DI, never through each other's database tables.
- Every feature has a `ServiceCollectionExtensions.AddXyz()` and, if it exposes HTTP endpoints, a `MapXyz()` extension.
- State that must survive restarts lives in SQLite (ADR-0006); hot, short-lived state (online agents, active relays, failure counters) lives in memory and is rebuilt on restart.

### Persistent data (`/data` volume)

| Path | Content |
|---|---|
| `/data/mkits.db` | SQLite database (WAL mode) |
| `/data/keys/` | ASP.NET Core Data Protection keys (cookie/token encryption), server signing keys |
| `/data/recordings/` | Encrypted session recordings |
| `/data/agents/` | Agent binaries offered for download / update |
| `/data/logs/` | Optional file logs (default: console only, collected by Docker) |

## 4. Agent internals

```
Agent.Core
├── Connection/     WSS client, reconnect with backoff, heartbeat, protocol negotiation
├── Crypto/         ECDH key agreement, AES-GCM channel encryption, SAS fingerprint
├── Sessions/       session state machine, consent UI contract
├── Channels/       video, input, clipboard, file, chat, shell handlers
└── Platform/       interfaces: IScreenCapture, IVideoEncoder, IInputInjector, IClipboard, IShellHost, IConsentPrompt
```

### Windows process model

```
 Session 0                                   Console session (user desktop)
┌──────────────────────────────┐   named   ┌────────────────────────────────────┐
│ MKITS Agent Service          │   pipe    │ Session helper (runs as SYSTEM in   │
│ (LocalSystem)                │◄─────────►│ the user's session)                 │
│ - server connection          │  (ACL:    │ - DXGI capture of Default AND       │
│ - follows active console     │  SYSTEM   │   Winlogon (secure) desktop         │
│   session (WTS events)       │  only)    │ - SendInput, clipboard              │
│ - starts helper via          │           │ - consent dialog / session banner   │
│   CreateProcessAsUser        │           │   (separate unprivileged UI process)│
│ - SendSAS (Ctrl+Alt+Del)     │           └────────────────────────────────────┘
└──────────────────────────────┘
```

- **Permanent agent:** installed service as above.
- **Ad-hoc agent:** single portable EXE. Starts unprivileged in the user's session (no secure desktop access). Offers *Restart with administrator rights*: the UAC prompt appears on the customer's screen; after elevation it installs a temporary service with the same process model and removes it when the session ends.

### Linux process model *(planned, Phase 11)*

- systemd system service (root) manages the connection and starts a per-session helper as the logged-in user.
- X11: XShm/XDamage capture and XTest input inside the user's display.
- Wayland: xdg-desktop-portal ScreenCast + RemoteDesktop via PipeWire; the portal asks the user for consent (restore tokens allow unattended re-use where the desktop supports it).

## 5. Main flows

### Ad-hoc support (ADR-0004)

1. Supporter (User/Admin) creates an ad-hoc session in the web UI → server returns an 8-digit **session code**.
2. Supporter tells the customer the code (phone).
3. Customer opens the portal (`https://<server>/`), enters the code → server validates it, offers the client download for the detected OS.
4. Client starts, reads server address and code from its file name (or asks), connects to `wss://<server>/ws/agent`, redeems the code.
5. Server links the agent to the session and notifies the supporter.
6. Supporter opens the viewer → agent shows the **consent dialog** → customer approves.
7. Agent and viewer perform the E2E key exchange through the relay → session active; banner visible on the customer's screen.
8. Either side ends the session; ad-hoc agent exits and deletes its temporary components.

### Permanent access (ADR-0013, ADR-0014)

1. Admin creates a device group and an enrollment token / installer package.
2. Agent is installed, enrolls with the token, generates its long-term key pair, registers its public key.
3. Agent keeps a persistent connection; device appears online.
4. Authorized User opens the device → consent policy of the group applies (*ask* / *notify* / *silent*) → E2E key exchange → active.

Details: [session-lifecycle.md](session-lifecycle.md). Wire format: [protocol.md](protocol.md). Security: [security-concept.md](security-concept.md). Entities: [data-model.md](data-model.md).

## 6. Capacity assumptions

Defaults sized for one server with ≤ 50 concurrent sessions and ≤ 1000 permanent devices (WORKPLAN Q18). Relay throughput is the limiting factor (~1–5 Mbit/s per active screen session). Idle permanent agents cost one WebSocket connection with a heartbeat every 30 s.
