# MKITS.SupportSuite.Server

The server: web UI (Blazor), customer portal, REST/WebSocket endpoints, session broker and relay, device registry,
IP bans, audit log, agent downloads and updates – one ASP.NET Core process with SQLite (ADR-0002, ADR-0006, ADR-0010). The big
picture is in [docs/architecture.md](../../../docs/architecture.md#3-server-internals); recipes for changes in
[docs/extending.md](../../../docs/extending.md).

| Folder | Content |
|---|---|
| `Program.cs` | the pipeline in order: forwarded headers → IP bans → rate limits → security headers → auth → endpoints |
| `Features/Identity` | users, roles, login, TOTP 2FA, recovery codes, first-run setup token, policies `Supporter`/`Admin` |
| `Features/IpBans` | failure counting per client network, automatic and manual bans, allow/deny lists |
| `Features/Notifications` | admin e-mail notifications (outbox, SMTP) |
| `Features/Audit` | append-only audit log with hash chain and integrity check |
| `Features/Sessions` | session codes (hashed), ad-hoc and device sessions, lifecycle, consent |
| `Features/Relay` | `/ws/agent` and `/ws/viewer`, `SessionHub` (routing, flow control, viewer limits), `PeerConnection`, viewer page and one-time tickets |
| `Features/Devices` | groups, permissions, enrollment tokens, device registry, signed device identities |
| `Features/Downloads` | customer portal, client catalog (`/data/agents`), ad-hoc file names, signed update manifests |
| `Infrastructure/Configuration` | `Mkits` options, data directory |
| `Infrastructure/Data` | `MkitsDbContext`, migrations (see [development.md](../../../docs/development.md#database-migrations-ef-core)) |
| `Infrastructure/Security` | security headers (CSP), in-app rate limits |
| `Infrastructure/ReverseProxy` | trusted proxies for `X-Forwarded-*` |
| `Infrastructure/Monitoring` | server metrics, Prometheus exporter, `/metrics` |
| `Infrastructure/Health`, `Endpoints` | `/health`, `/version` |
| `Components` | Blazor UI: `Account` (static SSR: login, setup, 2FA), `Pages` (sessions, devices), `Admin` (interactive) |
| `Resources` | UI texts EN/DE (`SharedResource`) |
| `wwwroot` | CSS; `viewer/viewer.js` is built from `src/Viewer/web` (not in git) |

Run: `dotnet run --project src/Server/MKITS.SupportSuite.Server` (http://localhost:5080, development settings).
Settings: administration guide § 10. Tests: `tests/MKITS.SupportSuite.Server.Tests` (unit, integration and relay
end-to-end against the test server) and `tests/e2e` (Playwright against the published server).
