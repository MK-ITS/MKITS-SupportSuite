# AGENTS.md – Instructions for AI coding agents

This file tells AI agents (Claude Code, Copilot, Codex, …) how to work in this repository.
Humans are welcome to read it too. Keep it short, current and authoritative.

## 1. What this project is

**MKITS-SupportSuite** – self-hosted remote support / remote maintenance software (comparable to ScreenConnect, RustDesk).
License **AGPL-3.0-only**. Public repository: https://github.com/MK-ITS/MKITS-SupportSuite

Components:

| Component | Tech | Path |
|---|---|---|
| Server (web UI, API, session broker, relay) | ASP.NET Core, .NET 10, SQLite (EF Core), Docker | `src/Server` |
| Common code (product info, protocol) | .NET 10 class library | `src/Common` |
| Agent core | .NET 10 class library, platform-independent | `src/Agent/*.Agent.Core` |
| Windows agent | .NET 10 (`net10.0-windows`) | `src/Agent/*.Agent.Windows` |
| Linux agent (X11 + Wayland) | .NET 10 | `src/Agent/*.Agent.Linux` |
| Browser viewer | TypeScript, Protobuf-ES, WebCrypto (npm, esbuild, vitest) | `src/Viewer/web` |
| Native viewer | .NET 10 + Avalonia (planned) | `src/Viewer/desktop` |
| Android agent | Kotlin (planned) | `android` |

## 2. Read before you change anything

1. **[WORKPLAN.md](WORKPLAN.md)** – requirements, architecture, decisions, roadmap. Find the task you are working on.
2. **[docs/adr/](docs/adr/README.md)** – architecture decisions. Do not contradict an accepted ADR; propose a new ADR that supersedes it instead.
3. **[docs/coding-guidelines.md](docs/coding-guidelines.md)** – coding rules.
4. **[docs/development.md](docs/development.md)** – branches, database migrations, diagnosing CI failures, test helpers.
5. The `README.md` of the module you are touching (if present).

The original German requirements are in [docs/requirements/original-requirements.de.md](docs/requirements/original-requirements.de.md).

## 3. Golden rules

1. **English everywhere internally** – code, comments, commit messages, docs, log messages. Only UI texts are bilingual.
2. **Every UI text is localized** in English **and** German (resource files). No hard-coded UI strings.
3. **Every source file starts with the license header** (the build fails otherwise – rule IDE0073):
   ```csharp
   // SPDX-License-Identifier: AGPL-3.0-only
   // Copyright (C) MK-ITSolutions and contributors
   ```
4. **Zero warnings.** Warnings are errors; code style is enforced in the build. Do not suppress analyzer rules without a comment explaining why.
5. **No secrets in the repository** – no keys, certificates, passwords, tokens, real customer data. The repo is public.
6. **Security first.** This software grants remote control over computers. Treat every input from the network as hostile. Changes to authentication, authorization, the relay, crypto, consent or agent privileges need an explanation in the PR and, if architectural, an ADR.
7. **Dependencies:** versions only in `Directory.Packages.props`. License must be AGPL-compatible (MIT, BSD, Apache-2.0, LGPL, GPL-3.0, AGPL-3.0). Add every new dependency to `THIRD-PARTY-NOTICES.md`.
8. **Modular and readable** over clever. Small classes, clear names, one responsibility. Platform-specific code stays in platform projects behind interfaces from `Agent.Core`.
9. **Keep docs in sync:** update `WORKPLAN.md` checkboxes, module READMEs and ADRs in the same change as the code.

## 4. Commands

```bash
dotnet restore MKITS.SupportSuite.slnx
dotnet build   MKITS.SupportSuite.slnx -c Release
dotnet test    MKITS.SupportSuite.slnx -c Release
dotnet format  MKITS.SupportSuite.slnx --verify-no-changes   # CI fails if this reports changes
dotnet run --project src/Server/MKITS.SupportSuite.Server      # http://localhost:5080
docker build -f deploy/docker/Dockerfile -t mkits-server .
# browser viewer (Node 22): tests and bundle → src/Server/.../wwwroot/viewer/viewer.js
cd src/Viewer/web && npm ci && npm test && npm run build
```

The Windows agent targets `net10.0-windows` but compiles on Linux (`EnableWindowsTargeting`). Running it requires Windows.

## 5. Conventions in short

- Namespaces = folder path, file-scoped: `MKITS.SupportSuite.<Component>.<Folder>`.
- Project names: `MKITS.SupportSuite.<Component>`; test projects: `<Project>.Tests` in `tests/`.
- Tests: xUnit, method names `Method_Scenario_ExpectedResult`.
- Async all the way; pass `CancellationToken`; use `ConfigureAwait(false)` in libraries.
- Configuration through the options pattern; never read environment variables directly in business code.
- Logging through `ILogger<T>` with structured templates; never log secrets, session codes, passwords or key material.

## 6. Definition of done

- [ ] Builds with zero warnings, all tests pass, `dotnet format --verify-no-changes` is clean
- [ ] Tests cover the new behavior (unit; integration tests for endpoints)
- [ ] UI texts in EN + DE
- [ ] Docs updated (WORKPLAN checkboxes, module README, ADR if a decision was made)
- [ ] Security impact considered

## 7. Commit messages

Conventional Commits in English: `feat(server): add IP ban service`, `fix(agent-windows): …`, `docs: …`, `chore(ci): …`.
Scopes: `server`, `common`, `agent-core`, `agent-windows`, `agent-linux`, `android`, `viewer-web`, `viewer-desktop`, `deploy`, `ci`, `docs`.
