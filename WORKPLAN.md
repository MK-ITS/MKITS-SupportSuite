# MKITS-SupportSuite – Work Plan

> Living work file. Source of truth for scope, decisions, open questions and the task roadmap.
> Written in English (project rule: everything internal is English). UI languages: German + English.
> Audience: the project owner **and future AI agents** that continue development.
> License: **AGPL-3.0**.

- **Created:** 2026-10-17 · **Last update:** 2026-10-17
- **Status:** Phases 1–3 done, Phase 4 (identity & security) done; Phase 5 (session broker & relay) done on the server; Phase 6 (web UI) mostly done; Phase 7 (agent core) in progress
- **Requirements source:** `_system_prompt.md`

---

## 1. Goal (summary of requirements)

Self-hosted remote support / remote maintenance suite (comparable to ConnectWise ScreenConnect, RustDesk).

| Area | Requirement |
|---|---|
| Client platforms | Windows 10 x64+ / Server 2016+, Linux (Debian, Ubuntu), Android 10+ |
| Session types | Graphical remote control of the **physical console session** (incl. login screen / UAC) **and** remote shell |
| Ad-hoc support | Supporter creates a session code → end user enters it on the web portal, downloads the client, session starts |
| Unattended access | Permanent connections to managed devices (set up by Admins), organized in device groups |
| Server | Ubuntu Linux 26.04+, delivered as Docker / Compose, web UI for administration and client join |
| Network | Behind reverse proxy (nginx) for HTTPS; **everything over 443** (relay only) |
| Roles | `Guest` (unauthenticated, customer view), `User` (customer view + act as supporter), `Admin` (User + permanent connections + administration) |
| Brute-force protection | fail2ban-like: after N failed logins (default 5) block source IP for 1 week; whitelist + blacklist configurable; e-mail notification |
| OS security | Respect UAC / secure desktop on Windows and equivalents on other OSes |
| Code quality | Modular, maintainable, human-readable |
| Docs | English, structured so AI agents can maintain/extend the project |
| Repository | Public GitHub repository (open source from day one), GitHub Actions CI |

---

## 2. Architecture

```
                         Internet (TCP 443 only)
                                  │
                           ┌──────▼──────┐
                           │   nginx     │  TLS termination, WebSocket upgrade,
                           │ (rev.proxy) │  X-Forwarded-For
                           └──────┬──────┘
                                  │ http://mkits-server:8080 (Docker network)
 ┌────────────────────────────────▼───────────────────────────────────┐
 │           MKITS Server – ASP.NET Core (.NET 10), Docker            │
 │  ┌────────────┐ ┌─────────────┐ ┌──────────────┐ ┌───────────────┐ │
 │  │ Web UI     │ │ REST API    │ │ Session      │ │ Relay         │ │
 │  │ Admin /    │ │ (auth, mgmt)│ │ Broker       │ │ (WSS streams, │ │
 │  │ Supporter /│ │             │ │ (codes,      │ │ E2E-encrypted │ │
 │  │ Portal     │ │             │ │ lifecycle)   │ │ payload)      │ │
 │  └────────────┘ └─────────────┘ └──────────────┘ └───────────────┘ │
 │  ┌────────────┐ ┌─────────────┐ ┌──────────────┐ ┌───────────────┐ │
 │  │ Identity   │ │ IP Ban      │ │ Device       │ │ Audit Log,    │ │
 │  │ Roles, TOTP│ │ Service     │ │ Registry,    │ │ Recordings,   │ │
 │  │            │ │ + SMTP      │ │ Groups,Update│ │ Mail          │ │
 │  └────────────┘ └─────────────┘ └──────────────┘ └───────────────┘ │
 │               SQLite (EF Core, WAL) on Docker volume               │
 └────────────────────────────────────────────────────────────────────┘
        ▲ WSS                                         ▲ WSS
        │                                             │
 ┌──────┴─────────────────────┐            ┌──────────┴────────────────┐
 │ Agents (end devices)       │            │ Viewers (supporter)       │
 │ - Windows: C#, service +   │            │ - Browser (primary, M1)   │
 │   per-session helper       │            │ - Native app (C#,         │
 │ - Linux: C#, systemd,      │            │   Avalonia, later)        │
 │   X11 + Wayland            │            └───────────────────────────┘
 │ - Android: Kotlin app      │
 └────────────────────────────┘
```

Key design points:
- **Single public port 443, relay only.** Agents and viewers connect outbound via WebSocket Secure through nginx. No inbound ports on end devices, no UDP/STUN/TURN.
- **Tech stack:** .NET 10 LTS / C# for server, Windows/Linux agents and native viewer. **Exception: Android agent in Kotlin** (direct access to MediaProjection, AccessibilityService). Shared protocol definition (e.g. Protobuf) so C#, Kotlin and TypeScript (browser) stay in sync.
- **Generic signed client binary.** Session code passed via file name (e.g. `MKITS-Support_123456.exe`) or typed in; the binary never needs per-session re-signing.
- **Windows privileged model:** service as `LocalSystem` launches a helper into the active console session (`WTSGetActiveConsoleSessionId` + `CreateProcessAsUser`) → secure desktop (UAC, login screen) capture/control and Ctrl+Alt+Del (`SendSAS`). Ad-hoc client runs unelevated by default and offers "restart elevated".
- **End-to-end encryption:** agent and viewer agree on a session key (ECDH, X25519/P-256 via WebCrypto in browser) and encrypt the payload (AES-GCM); the server only routes. Fingerprint shown to both sides.
- **Session recording vs. E2E (design conflict):** with E2E the server cannot record. Recording is therefore done **by the viewer/agent** and uploaded encrypted, or recording is a group policy that requires it explicitly. → To be specified in ADR (task 2.4).
- **Reverse proxy awareness:** trusted-proxy list; real client IP from `X-Forwarded-For` only from trusted proxies (required for IP bans).
- **IP Ban Service** covers login, TOTP, session-code entry (anti code guessing), agent enrollment.
- **SQLite** keeps the deployment simple; data access via EF Core behind repository interfaces so PostgreSQL can be added later if scale requires it.

---

## 3. Open questions

Answered questions are moved to section 4. Remaining questions use the **proposed default** until confirmed.

| ID | Question | Proposed default |
|---|---|---|
| Q18 | Expected scale: concurrent sessions, number of permanent devices? (relevant for SQLite and relay bandwidth) | ≤ 50 concurrent sessions, ≤ 1000 devices on one server |
| Q20 | Branding: logo, colors, domain for test/production (e.g. `support.mk-itsolutions.tech`)? | Placeholder branding, configurable in admin UI |
| Q21 | IP ban details: count window? ban duration configurable? | 5 failures within 10 min → 7 days ban, all values configurable |
| Q22 | Native viewer platforms (Windows only, or also Linux/macOS)? | Windows + Linux (Avalonia), after M1 |
| Q23 | Recording storage and retention (how long, who may watch)? Legal basis (GDPR / works council)? | Stored on server volume, 30 days retention, Admins only |

---

## 4. Decisions (ADR log)

Decisions are recorded here and as individual files under [`docs/adr/`](docs/adr/README.md).

| ADR | Q | Decision |
|---|---|---|
| ADR-0000 | – | License AGPL-3.0; English for code/docs; UI German + English (per-user UI culture) |
| ADR-0001 | Q1 | Both: graphical remote control of the physical console session **and** remote shell |
| ADR-0002 | Q2 | .NET 10 / C# for server, Windows/Linux agents, native viewer; Android agent native Kotlin (Q12) |
| ADR-0003 | Q3 | Viewer: browser (primary, part of M1) **and** native viewer app (later) |
| ADR-0004 | Q4 | Ad-hoc: supporter creates the session code, customer enters it on the portal |
| ADR-0005 | Q5 | Transport: relay-only via WSS over port 443, no P2P |
| ADR-0006 | Q6 | Database: SQLite (EF Core, WAL mode) |
| ADR-0007 | Q7 | MVP includes: screen view/control, chat, file transfer, clipboard, remote shell |
| ADR-0008 | Q8 | Authentication: local accounts + TOTP 2FA (no OIDC/LDAP for now) |
| ADR-0009 | Q9 | No code-signing certificate yet; develop unsigned, obtain one (e.g. Azure Trusted Signing) before production |
| ADR-0010 | Q10 | Server deployment: Docker / Compose |
| ADR-0011 | Q11 | Linux GUI: X11 **and** Wayland in the first Linux release |
| ADR-0012 | Q12 | Android: accept OS limitations (consent per session, Accessibility Service), native Kotlin app, APK download from own server |
| ADR-0013 | Q13 | Permanent devices organized in groups; Users get access per group, Admins see everything |
| ADR-0014 | Q14 | Consent: ad-hoc always requires approval + visible banner; permanent devices configurable per group (ask / notify / silent) |
| ADR-0015 | Q15–Q16, Q19, Q21 | Planned features: session recording, end-to-end encryption, agent auto-update (signed), e-mail notification on IP bans |
| ADR-0016 | Q17 | Public GitHub repository + GitHub Actions |
| ADR-0017–0019 | – | Protobuf; E2E + recording; ad-hoc client config via file name |
| ADR-0020 | – | Blazor Web App (static SSR + Interactive Server) for the UI; Visual Studio 2026 compatible |

---

## 5. Roadmap / task list

Legend: `[ ]` open · `[~]` in progress · `[x]` done.
Milestones: **M1** = Windows ad-hoc support via browser (MVP) · **M2** = permanent connections · **M3** = Linux · **M4** = Android · **M5** = native viewer, recording, hardening.

### Phase 0 – Clarification & decisions
- [x] 0.1 Read `_system_prompt.md`, derive requirements
- [x] 0.2 Create this work file
- [x] 0.3 Answers to Q1–Q17
- [ ] 0.4 Confirm Q18, Q20–Q23 (defaults apply meanwhile)
- [x] 0.5 Write ADR files under `docs/adr/`

### Phase 1 – Repository foundation
- [x] 1.1 Public GitHub repo, branch strategy (main + feature branches, PRs), `.gitignore`, `.editorconfig`
- [x] 1.2 `LICENSE` (AGPL-3.0), license headers, `THIRD-PARTY-NOTICES.md`
- [x] 1.3 `README.md`, `AGENTS.md` + `CLAUDE.md` (AI agent instructions), `CONTRIBUTING.md`, `SECURITY.md` (vulnerability disclosure – important for a public remote-access tool)
- [x] 1.4 .NET solution + folder structure: `src/Common`, `src/Server`, `src/Agent/{Core,Windows,Linux}`, `src/Viewer/{web,desktop}`, `android/`, `docs/`, `deploy/`, `tests/`
- [x] 1.5 Coding conventions, analyzers, `dotnet format`, `Directory.Build.props`, central package management
- [x] 1.6 GitHub Actions: build + test (Ubuntu, Windows), format check, CodeQL, Dependabot, Docker image build
- [ ] 1.7 Repository settings on GitHub (owner): branch protection for `main`, private vulnerability reporting, secret scanning + push protection

### Phase 2 – Architecture & protocol specification
- [x] 2.1 `docs/architecture.md` (components, responsibilities, data flow)
- [x] 2.2 Wire protocol spec: framing over WSS, channels (video, input, clipboard, file, chat, shell, control), versioning, Protobuf schema
- [x] 2.3 Session lifecycle spec: code generation/expiry, join, consent, banner, end
- [x] 2.4 Security concept: threat model, tokens, agent identity/enrollment, E2E key exchange, recording vs. E2E, update signing
- [x] 2.5 Data model: users, roles, TOTP, sessions, devices, groups, permissions, bans, audit, recordings

### Phase 3 – Server core
- [x] 3.6 Security headers (CSP, frame/sniff/referrer policies), no `Server` header
- [x] 3.1 ASP.NET Core host, validated configuration (appsettings + env), health endpoint incl. data directory check (logging: built-in console logging for now; Serilog not needed yet)
- [x] 3.2 EF Core + SQLite → done in 4.0
- [x] 3.3 Forwarded headers / trusted proxies, configurable ports
- [x] 3.4 Localization de/en (resource files, per-user UI culture, browser fallback)
- [x] 3.5 `Dockerfile`, `docker-compose.yml` (server + optional nginx), volumes, sample nginx config (TLS, WebSocket upgrade, timeouts)

### Phase 4 – Identity & security
- [x] 4.0 EF Core + SQLite (WAL), first migration, automatic migration at startup (migrations via `ef-migration.yml`, see docs/development.md)
- [x] 4.0a First-run setup token + `/setup` page (docs/security-concept.md §4)
- [x] 4.1 User management: Identity model, roles, password policy, lockout, admin UI (`/admin/users`, self-/last-admin protection)
- [x] 4.2 Login/logout, cookie security (`__Host-`, SameSite=Strict), CSRF, TOTP 2FA (QR, recovery codes, mandatory for Admins), profile + language
- [x] 4.3 IP Ban Service: fail counter, 7-day ban, CIDR allow/deny lists (config + DB), middleware, admin UI (`/admin/ip-bans`), SMTP notification (throttled, localized)
- [~] 4.4 Rate limiting: nginx limits for auth/setup/code endpoints ✅; in-app limits follow with the session-code and enrollment endpoints (Phases 5/10)
- [x] 4.5 Audit log: hash-chained store + verification, admin UI (`/admin/audit`, filter, paging, integrity check); events are added by each feature. CSV export open

### Phase 5 – Session broker & relay
- [x] 5.1 Session codes (supporter-created, expiry, single use) – `Features/Sessions`, page `/sessions`, protobuf code generation in Common
- [x] 5.2 WebSocket endpoints for agents and viewers, authentication – `Features/Relay` (`/ws/agent`, `/ws/viewer`, ad-hoc code, viewer ticket, resume token)
- [x] 5.3 Relay routing per channel, backpressure, reconnect – grace period 60 s, heartbeat 30/90 s, video drop at 4 MiB, disconnect at 16 MiB
- [~] 5.4 Consent flow + session banner – server side done (request per viewer, timeout, deny); banner/dialog in the agents (Phase 7/8)
- [~] 5.5 E2E encryption handshake (agent ↔ viewer), fingerprint display – crypto library `Common/Crypto` (ECDH P-256, HKDF, AES-256-GCM, replay protection, security code) done; relay forwards `KeyExchange`; display in agent/viewer UIs (Phases 7–9)

### Phase 6 – Web UI
- [~] 6.1 Customer portal: code entry → client download (OS detection), Guest session start – portal + download with code/host in the file name done (`Features/Downloads`); session start needs the client (Phase 8)
- [x] 6.2 Supporter area: create ad-hoc session, session list, join – `/sessions` (create/list/revoke/open), viewer page `/sessions/{id}/view`
- [x] 6.3 Admin area: users, roles, settings, SMTP, IP bans, audit log (Phase 4)
- [x] 6.4 i18n de/en for all texts, AGPL source link in footer

### Phase 7 – Agent core (C#, shared Windows/Linux)
- [~] 7.1 Connection management (WSS, reconnect, heartbeat), protocol, E2E crypto – `AgentSession` (handshake, code/resume auth, consent callback, key exchange per viewer, encrypted dispatch, reconnect) done, tested end-to-end against the relay
- [ ] 7.2 Configuration, logging, ad-hoc vs. permanent mode
- [x] 7.3 Abstractions: capture → encode → send, input, clipboard, file system, shell (PTY / ConPTY) – `IScreenSource`, `IInputInjector`, `IClipboard`, `IChatPresenter`, `IFileTransferUi`, `IShellHost` done
- [x] 7.4 Chat, file transfer, clipboard, remote shell implementations – `ScreenStreamer` (flow control, change detection, per-viewer fps/monitor), `InputHandler` (coordinate mapping, stuck-key release), `ClipboardHandler`, `ChatHandler`, `FileTransferHandler` (consent per file, sanitized names, in-order chunks, SHA-256, no overwrite), `ShellHandler` (platform-defined shells only, ≤ 3 per viewer, closed when the viewer leaves) done

### Phase 8 – Windows agent
- [~] 8.1 Screen capture (DXGI Desktop Duplication, multi-monitor, GDI fallback) – GDI capture, multi-monitor done (`GdiScreenSource`); DXGI open
- [~] 8.2 H.264 encoding (Media Foundation hardware / OpenH264), JPEG fallback – JPEG done; H.264 open
- [~] 8.3 Input injection (SendInput, keyboard layouts, special keys) – SendInput with scan codes (layout-independent), Unicode text, Win/Alt+Tab/PrintScreen done; Ctrl+Alt+Del needs the service (8.4)
- [~] 8.4 Service (LocalSystem) + session helper, secure desktop / UAC, Ctrl+Alt+Del (SendSAS), session switching – elevated ad-hoc mode (ADR-0021): temporary service, SYSTEM helper following the input desktop, SendSAS, helper restart on console switch; permanent service follows in Phase 10
- [~] 8.5 Remote shell via ConPTY (PowerShell / cmd), optionally elevated – ConPTY with user rights done (`ConPtyShellHost`); elevated shell needs the service (8.4)
- [~] 8.6 Ad-hoc portable EXE (code from file name or prompt, "restart elevated") – single-file EXE (CI artifact), file name / dialog, WinForms UI (consent, status, security code, chat, end) done; "restart elevated" done (ADR-0021)

### Phase 9 – Browser viewer
- [~] 9.1 Canvas + WebCodecs H.264 decoder, fallback – canvas with JPEG (flow-control acks, letterbox-aware scaling) done; WebCodecs/H.264 follows with the agent encoder
- [x] 9.2 Mouse/keyboard mapping, special key toolbar – pointer (throttled moves, buttons, wheel), physical keys (`KeyboardEvent.code`), Ctrl+Alt+Del / Win / Alt+Tab
- [~] 9.3 Multi-monitor, scaling, quality settings – monitor selection, fit-to-window, default quality; quality UI open
- [x] 9.4 Chat, file transfer, clipboard, terminal (xterm.js) – chat, clipboard (buttons, browser permission) file transfer both ways (incremental SHA-256) and terminal (xterm.js, several shells) done
- [x] 9.5 E2E crypto via WebCrypto – `src/Viewer/web/src/crypto.ts`, byte-compatible with C# (shared test vector `proto/testvectors/e2e-v0.json`)

**→ M1: Windows ad-hoc remote support end-to-end (portal → download → consent → control + chat/file/clipboard/shell)**

### Phase 10 – Permanent connections (Admin)
- [~] 10.1 Device registry, groups, per-group permissions for Users, online status, metadata – 10A-1: data model, DeviceService (groups, permissions, hashed enrollment tokens, enrollment, ECDSA device authentication), admin page `/admin/devices`; 10A-2: enrollment and device-key authentication on the relay, online presence, supporter page `/devices`
- [ ] 10.2 Deployment package per group (MSI with enrollment token, WiX)
- [~] 10.3 Unattended access incl. reboot/reconnect, consent policy per group (ask / notify / silent) – server side done in 10A-2 (permanent sessions on the presence connection, rebind after reconnect, policies, per-channel permission enforcement); 10A-3: agent-core permanent mode (`DeviceAgent`: enrollment, protected identity store, idle presence, sessions over the presence connection, reconnect with backoff), agent-side permission checks, signed key exchange verified by the browser viewer; the Windows service follows in 10B
- [ ] 10.4 Signed agent auto-update

**→ M2: permanent Windows devices**

### Phase 11 – Linux agent (Debian/Ubuntu)
- [ ] 11.1 X11 capture/input (XShm/XDamage, XTest)
- [ ] 11.2 Wayland via xdg-desktop-portal (ScreenCast + RemoteDesktop, PipeWire); GNOME and KDE; unattended via portal restore tokens / gnome-remote-desktop – limits documented
- [ ] 11.3 Login screen (GDM/LightDM/SDDM) access where possible
- [ ] 11.4 Remote shell (PTY), privilege handling (polkit/sudo)
- [ ] 11.5 systemd unit, `.deb` packages (amd64, arm64)

**→ M3: Linux**

### Phase 12 – Android agent (Kotlin, 10+)
- [ ] 12.1 App skeleton, foreground service, server connection, shared protocol
- [ ] 12.2 MediaProjection + MediaCodec H.264
- [ ] 12.3 Input via AccessibilityService
- [ ] 12.4 Consent UI, permission onboarding, chat/file/clipboard
- [ ] 12.5 Signed APK, download from own server

**→ M4: Android**

### Phase 13 – Native viewer & extended features
- [ ] 13.1 Native viewer (C#, Avalonia), same protocol as browser viewer
- [ ] 13.2 Session recording (per ADR from 2.4), retention, playback in web UI
- [ ] 13.3 Further hardening based on security review

**→ M5**

### Phase 14 – Deployment & operations
- [x] 14.1 Published Docker image (GHCR), versioned tags (workflow `publish.yml`; `edge` from main, semver from tags)
- [x] 14.2 Installation guide (Ubuntu 26.04 + Docker + nginx + Let's Encrypt), backup/restore, upgrade procedure – `docs/guides/{en,de}/installation.md` (extend with each feature)
- [ ] 14.3 Monitoring/metrics, log rotation

### Phase 15 – Quality & security assurance
- [ ] 15.1 Unit + integration tests per module
- [ ] 15.2 End-to-end tests (Playwright for web UI, agent ↔ server)
- [ ] 15.3 Security review (OWASP ASVS, dependency audit, pen test portal/relay/E2E)
- [ ] 15.4 Test matrix: Windows 10/11, Server 2016–2025, Debian 12/13, Ubuntu LTS (X11 + Wayland), Android 10–16

### Phase 16 – Documentation & release
- [~] 16.1 User docs (de/en): administration guide written (`docs/guides/{en,de}/administration.md`, target behavior marked as planned); customer and supporter guides open
- [ ] 16.2 Developer/agent docs: architecture, module READMEs, ADRs, how-to-extend
- [ ] 16.3 Release process (SemVer), changelog, signed release artifacts

---

## 6. Known risks & constraints

- **Android unattended access** is restricted by the OS (consent per session, Accessibility Service). Accepted (ADR-0012).
- **Wayland** capture depends on desktop environment and portal; fully unattended access is not guaranteed on all distributions. X11 + Wayland in one release increases the Linux effort significantly (ADR-0011).
- **E2E encryption vs. server-side recording** conflict – needs a dedicated design decision (task 2.4).
- **SmartScreen / antivirus** flag unsigned remote tools; a code-signing certificate is mandatory before production (ADR-0009).
- **Public repository** of a remote-access tool: no secrets in the repo, `SECURITY.md`, careful handling of reported vulnerabilities; scammers abuse remote tools → strong consent UI and visible banners.
- **Relay-only** costs server bandwidth (~1–5 Mbit/s per active session).
- **SQLite** is fine for the expected scale (Q18); revisit if load grows.
- **AGPL-3.0**: all dependencies must be compatible (e.g. OpenH264 BSD, Avalonia MIT, xterm.js MIT).

---

## 7. Change log of this file

| Date | Change |
|---|---|
| 2026-10-17 | Initial version: requirements, draft architecture, open questions Q1–Q21, roadmap |
| 2026-10-17 | Answers Q1–Q17 recorded as ADR-0001…0016; architecture and roadmap updated (SQLite, Docker, both viewers, Wayland in v1, E2E, recording, public GitHub); new questions Q22–Q23 |
| 2026-10-17 | Phase 1 done: solution skeleton (Common, Server, Agent.Core/Windows/Linux, tests), build rules, CI, docs, ADR files. Replaced the repository's initial GPL-3.0 `LICENSE` with AGPL-3.0. Project `Shared` named `Common` (CA1716: `Shared` is a VB keyword). |
| 2026-10-17 | Phase 2 done (architecture, protocol + proto schema, session lifecycle, security concept, data model, ADR-0017…0019). Phase 3 infrastructure (options, trusted proxies, data directory, Data Protection, security headers, localization). Compose/nginx templates, GHCR publish workflow, installation + administration guides EN/DE. |
| 2026-10-18 | Phase 4A: SQLite/EF Core with Identity, hash-chained audit log, IP ban service + middleware, EF migration workflow, docs/development.md. CI now also runs for stacked PRs. |
| 2026-10-18 | Phase 4B: Blazor UI (ADR-0020) with setup token, login, TOTP, recovery codes, profile/language, user administration; nginx header inheritance bug fixed (X-Forwarded-For was lost in locations); .vsconfig for Visual Studio 2026. |
| 2026-10-19 | PRs #7–#9 merged. Phase 4C: admin pages for IP bans/lists, audit log viewer with integrity check, settings page with SMTP test mail, throttled localized admin notifications (MailKit). |
| 2026-10-19 | PR #10 merged (MailKit 4.16.0, patched System.Security.Cryptography.Pkcs pinned transitively). Phase 5A: C# code generation from `proto/` (Google.Protobuf), `FrameCodec`, session codes (hashed, single use, limits, expiry), viewer tickets, supporter page `/sessions`. |
| 2026-10-19 | Phase 5B: WebSocket relay (`/ws/agent`, `/ws/viewer`), handshake + authentication, session broker with consent per viewer, suspend/resume (resume token, protocol 0.2), heartbeat, timeouts, backpressure, routing rules. |
| 2026-10-19 | Phase 5C: E2E session crypto in Common (`SessionCrypto`, `SessionCipher`, security code) with tests. |
| 2026-10-20 | PR #11 merged. Phase 6A: customer portal on the start page (code check with IP-ban counting, OS detection, POST download with `MKITS-Support_<host>[+port]_<code>` file name, `AdHocClientFileName` in Common), client catalog in `/data/agents`, settings section, guides. |
| 2026-10-20 | Phase 7A: agent core `AgentSession` + UI/channel-handler contracts, `PayloadChannels` in Common; end-to-end tests agent ↔ relay ↔ viewer with real crypto. |
| 2026-10-20 | PRs #13–#15 merged. Phase 7B: agent feature handlers (screen streaming, input, clipboard, chat) with platform abstractions and viewer lifecycle hooks; new test project Agent.Core.Tests. |
| 2026-10-20 | Phase 8A: Windows portable client – WinForms UI (consent, status, supporters, security code, chat, end), GDI/JPEG capture, SendInput (scan codes, Unicode), clipboard; `ScanCodes` in Agent.Core; CI publishes the unsigned EXE as artifact. |
| 2026-10-20 | PR #16 merged. Phase 9A: browser viewer (TypeScript, Protobuf-ES, WebCrypto) with viewer page `/sessions/{id}/view` (one-time ticket, no inline script), screen/input/chat/clipboard/special keys; shared E2E test vector checked by C# and TypeScript; server re-announces ACTIVE after an agent resume (viewers re-key); CI and Docker build the viewer bundle. |
| 2026-10-21 | PRs #17/#18 merged. Phase 7C: file transfer both ways – agent core handler (T14 rules), Windows UI (confirm dialog, *Send file*), viewer (accept/decline, upload, download) with incremental SHA-256. |
| 2026-10-21 | Phase 7D: remote shell – `ShellHandler` (agent core), ConPTY PowerShell/cmd (Windows), xterm.js terminals in the viewer; per-endpoint CSP override for the viewer page (inline styles for xterm only). |
| 2026-10-21 | PR #20 merged. Phase 8B: elevated ad-hoc mode on Windows (ADR-0021) – UAC → temporary LocalSystem service → SYSTEM helper on the input desktop (secure desktop, lock screen, Ctrl+Alt+Del); client keeps the connection and forwards capture/input over a protected pipe; self-removal. |
| 2026-10-21 | PR #21 merged. Phase 10A-1: permanent devices – data model (DeviceGroups, GroupAccess, Devices, EnrollmentTokens; migration AddDevices), `DeviceService` (groups, per-group permissions, hashed single-display enrollment tokens with atomic use counting, enrollment with P-256 key validation, ECDSA device authentication over the handshake nonce), admin page `/admin/devices`, audit events `group.*`, `enrollment_token.*`, `device.*`. |
| 2026-10-21 | PR #22 (10A-1) merged. Phase 10A-2 (PR #23): permanent devices on the relay – enrollment and ECDSA device authentication, idle presence (connection outlives sessions), `DeviceSessionLauncher` (create or join a device session), consent policies ask/notify/silent, per-viewer channel permissions enforced by the relay, device disable/remove ends sessions and disconnects; protocol 0.3 (`ViewerJoined.permissions`); supporter page `/devices`. |
| 2026-10-21 | PR #23 (10A-2) merged. Phase 10A-3 (PR #24): agent core permanent mode – `SessionEngine` (shared by ad-hoc and permanent agents, viewer permissions enforced on the agent too), `DeviceAgent` (enrollment, device-key authentication, idle presence, backoff, pending approval polling), `DeviceIdentity` + `FileDeviceIdentityStore`; `ChannelPermissions` in Common (single mapping for relay and agent); permanent agents sign their ephemeral key and the browser viewer verifies it against the enrolled device key; end-to-end tests with the real agent. |
