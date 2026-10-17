# ADR-0000: License AGPL-3.0, English internally, UI in English and German

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

The project owner wants an open-source product. Development and maintenance will partly be done by AI agents, which work best with English sources. End users are German- and English-speaking.

## Decision

- License: **AGPL-3.0-only** for all components.
- Code, comments, logs, docs and commit messages: **English**.
- User interfaces: **English and German**, selected by the user's UI culture.

## Consequences

- Every source file carries an SPDX header (enforced by IDE0073).
- Dependencies must be AGPL-compatible.
- The web UI must link to the source code (AGPL § 13).
- All UI strings live in resource files with EN + DE variants.
