# MKITS-SupportSuite

Self-hosted, open-source remote support and remote maintenance suite – ad-hoc support sessions via session code and permanent (unattended) connections to managed devices.

> **Status: early development (v0.1.0-dev).** Not usable yet. See [WORKPLAN.md](WORKPLAN.md) for the roadmap.

## Features (planned)

- **Ad-hoc support:** supporter creates a session code, the customer enters it on the web portal, downloads the client and approves the connection.
- **Permanent connections:** Admins enroll devices into groups for unattended access with per-group consent policies.
- **Platforms:** Windows 10 x64+ / Server 2016+, Linux (Debian, Ubuntu – X11 and Wayland), Android 10+.
- **Session types:** graphical control of the physical console session (including login screen and UAC prompts) and remote shell.
- **In-session tools:** chat, file transfer, clipboard sync, multi-monitor.
- **Viewer:** in the browser (no installation) and a native desktop viewer.
- **Security:** everything over HTTPS port 443 behind a reverse proxy, end-to-end encrypted sessions, TOTP two-factor authentication, fail2ban-style IP blocking, audit log, session recording.
- **Roles:** Guest, User, Admin. **UI languages:** English, German.

## Architecture at a glance

```
Agent (end device) ──WSS/443──► nginx ──► MKITS Server (Docker) ◄──WSS/443── Viewer (browser / desktop)
```

The server relays end-to-end encrypted streams between agents and viewers. Details: [WORKPLAN.md § Architecture](WORKPLAN.md#2-architecture) and [docs/](docs/README.md).

## Repository layout

| Path | Content |
|---|---|
| `src/Common` | Code shared by all .NET components (product info, protocol) |
| `src/Server` | ASP.NET Core server: web UI, API, session broker, relay |
| `src/Agent` | Agent core + Windows and Linux agents |
| `src/Viewer` | Browser viewer (`web`) and native viewer (`desktop`) |
| `android` | Android agent (Kotlin) |
| `tests` | Automated tests |
| `deploy` | Docker, nginx and installation assets |
| `docs` | Architecture, ADRs, guidelines |

## Building

Requirements: [.NET SDK 10](https://dotnet.microsoft.com/download) (version pinned in `global.json`).

```bash
dotnet build MKITS.SupportSuite.slnx -c Release
dotnet test  MKITS.SupportSuite.slnx -c Release
```

Run the server locally:

```bash
dotnet run --project src/Server/MKITS.SupportSuite.Server
# → http://localhost:5080/health  and  http://localhost:5080/api/system/version
```

Docker image:

```bash
docker build -f deploy/docker/Dockerfile -t mkits-server .
docker run --rm -p 8080:8080 -e Mkits__PublicBaseUrl=http://localhost:8080 -v mkits-data:/data mkits-server
```

## Installation and administration

- Installation: [English](docs/guides/en/installation.md) · [Deutsch](docs/guides/de/installation.md)
- Administration: [English](docs/guides/en/administration.md) · [Deutsch](docs/guides/de/administration.md)

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). AI coding agents: start with [AGENTS.md](AGENTS.md).

## Security

Please report vulnerabilities privately – see [SECURITY.md](SECURITY.md).

## License

Copyright (C) MK-ITSolutions and contributors.

This program is free software: you can redistribute it and/or modify it under the terms of the
[GNU Affero General Public License v3.0](LICENSE) as published by the Free Software Foundation.
If you run a modified version as a network service, you must offer its source code to its users (AGPL-3.0 § 13).
