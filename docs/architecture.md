# Architecture

> Status: **implemented** for the server, the browser viewer and the Windows and Linux agents (state before the first
> release). Parts marked *(planned)* are not implemented yet. Decisions referenced as ADR-NNNN live in
> [adr/](adr/README.md); how to add things is in [extending.md](extending.md).

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
| Windows agent | `src/Agent/MKITS.SupportSuite.Agent.Windows` | DXGI/GDI capture, Media Foundation H.264, SendInput, ConPTY, SYSTEM helper, installed service (WinForms UI) |
| Linux agent | `src/Agent/MKITS.SupportSuite.Agent.Linux` | X11 (XTest) and Wayland (portal + PipeWire) capture/input, PTY, systemd service, Avalonia UI |
| Android agent | `android/` | Kotlin app: MediaProjection, MediaCodec, AccessibilityService *(planned, Phase 12)* |
| Browser viewer | `src/Viewer/web` | TypeScript: canvas + WebCodecs (H.264) / JPEG, input, WebCrypto, xterm.js |
| Desktop viewer | `src/Viewer/desktop` | Avalonia app *(planned)* |

## 3. Server internals

The server is a single ASP.NET Core process, organized **by feature** (see [coding-guidelines.md](coding-guidelines.md)):

```
Server
├── Infrastructure/         cross-cutting: configuration + data directory, reverse proxy (forwarded headers),
│                           localization, security (headers, rate limits), data access (EF Core/SQLite),
│                           health checks, monitoring (metrics, Prometheus exporter)
├── Features/
│   ├── Identity/           users, roles, login, TOTP, first-run setup token
│   ├── IpBans/             failure tracking, bans, allow/deny lists
│   ├── Notifications/      admin e-mail notifications (outbox)
│   ├── Audit/              append-only, hash-chained audit log
│   ├── Sessions/           session codes, lifecycle, consent
│   ├── Relay/              WebSocket endpoints, session hub, routing, backpressure, viewer page and tickets
│   ├── Devices/            groups, permissions, enrollment tokens, device registry
│   ├── Downloads/          customer portal, client catalog, ad-hoc file naming, signed agent updates
│   └── Recordings/         storage, retention, playback                            (planned, Phase 13)
├── Components/             Blazor UI (ADR-0020): App, Routes, Layout, Pages, Account (static SSR), Admin (interactive)
├── Endpoints/              system endpoints (health, version)
├── wwwroot/                static assets (css)
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
| `/data/keys/` | ASP.NET Core Data Protection keys (cookie/token encryption) |
| `/data/agents/` | Client and agent packages offered for download and update, with the signed update manifests |
| `/data/recordings/` | Encrypted session recordings *(planned)* |

Logs go to the console (collected and rotated by Docker).

## 4. Agent internals

```
Agent.Core (platform-independent)
├── Connection/     WebSocket client, frame codec, protocol negotiation
├── Sessions/       AgentSession (ad-hoc), SessionEngine (frames, E2E key exchange, consent, permissions),
│                   IAgentUserInterface / ISessionContext
├── Devices/        DeviceAgent (installed agents: enrollment, identity key, reconnect with backoff)
├── Features/       channel handlers: ScreenStreamer (JPEG/H.264 per viewer), InputHandler, ChatHandler,
│                   ClipboardHandler, FileTransferHandler, ShellHandler (+ ElevatedShellRouter);
│                   platform interfaces IScreenSource, IInputInjector, IClipboard, IShellHost, …
├── Bridge/         UserSessionBridge: forwards channels from a service into the user's session process
├── Video/          NV12 conversion, Annex B helpers for the encoders
├── Updates/        signed update manifests, version comparison
└── Logging/        LineLoggerProvider and the core's log messages
```

Platform projects implement the interfaces and own everything with native APIs (`Native/` P/Invoke only). The core
never references a platform project.

### Windows process model

```
 Session 0                                    User's session
┌───────────────────────────────┐  named   ┌─────────────────────────────────────────┐
│ MKITSAgent service (SYSTEM)   │  pipes   │ SYSTEM helper (--agent-helper)           │
│ - server connection, identity │◄────────►│ - follows the input desktop (Default ↔   │
│ - consent policy, SendSAS     │ (ACL +   │   Winlogon): DXGI/GDI capture, H.264,     │
│ - admin shells (ConPTY)       │  client  │   SendInput                               │
│ - starts helper and session   │  PID     ├─────────────────────────────────────────┤
│   window per console session  │  check)  │ Session window (--agent-user, the user)   │
│ - auto-update (signed)        │          │ - consent dialog, banner, chat, files,    │
└───────────────────────────────┘          │   clipboard, user shells (ConPTY)         │
                                           └─────────────────────────────────────────┘
```

- **Installed agent** (ADR-0022): as above; the service also works with nobody logged on (login screen, admin
  shells).
- **Ad-hoc client** (ADR-0021): one portable EXE in the customer's session. *Restart with administrator rights* (UAC)
  installs a temporary service that starts the SYSTEM helper for the secure desktop and admin shells; the client keeps
  the server connection and switches capture/input to the helper; everything is removed with the session.

### Linux process model (ADR-0025, ADR-0026)

- **Installed agent:** systemd service (root) with the server connection and identity; it finds the graphical session
  through logind and starts a session window as the logged-on user (`runuser`, stdio bridge) for consent, banner,
  chat, files and clipboard. Root shells run in the service.
- **X11:** the service opens the user's display with the X server's cookie; capture via `XGetImage`, input via XTest;
  the X11 login screen works too.
- **Wayland:** the session window uses xdg-desktop-portal ScreenCast + RemoteDesktop with PipeWire (the desktop asks
  once; restore tokens make it unattended where supported).
- **Ad-hoc client:** AppImage (x86_64, arm64) running as the user, X11 or Wayland as above.

## 5. Main flows

### Ad-hoc support (ADR-0004)

1. Supporter (User/Admin) creates an ad-hoc session in the web UI → server returns an 8-digit **session code**.
2. Supporter tells the customer the code (phone).
3. Customer opens the portal (`https://<server>/`), enters the code → server validates it, offers the client download for the detected OS.
4. Client starts, reads server address and code from its file name (or asks), connects to `wss://<server>/ws/agent`, redeems the code.
5. Server links the agent to the session and notifies the supporter.
6. Supporter opens the viewer → agent shows the **consent dialog** → customer approves.
7. Agent and viewer perform the E2E key exchange through the relay (the viewer verifies the device-signed key of
   installed agents) → session active; banner and security code visible on both sides.
8. Either side ends the session; ad-hoc agent exits and deletes its temporary components.

### Permanent access (ADR-0013, ADR-0014)

1. Admin creates a device group and an enrollment token; the agent is installed with `--install --server … --token …`
   (Windows EXE, Linux .deb).
2. Agent is installed, enrolls with the token, generates its long-term key pair, registers its public key.
3. Agent keeps a persistent connection; device appears online.
4. Authorized User opens the device → consent policy of the group applies (*ask* / *notify* / *silent*) → E2E key exchange → active.

Details: [session-lifecycle.md](session-lifecycle.md). Wire format: [protocol.md](protocol.md). Security: [security-concept.md](security-concept.md). Entities: [data-model.md](data-model.md).

## 6. Capacity assumptions

Defaults sized for one server with ≤ 50 concurrent sessions and ≤ 1000 permanent devices (WORKPLAN Q18). Relay throughput is the limiting factor (~1–5 Mbit/s per active screen session). Idle permanent agents cost one WebSocket connection with a heartbeat every 30 s.
