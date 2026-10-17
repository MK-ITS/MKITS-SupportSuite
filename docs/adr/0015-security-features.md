# ADR-0015: Planned security features

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Additional protection and traceability were requested.

## Decision

Planned: **end-to-end encryption** between agent and viewer, **session recording**, **signed agent auto-update**, **e-mail notification** on IP bans.

## Consequences

- E2E encryption and server-side recording conflict; a dedicated ADR (Phase 2.4) will define how recording works with E2E.
- SMTP configuration is needed in the admin UI.
