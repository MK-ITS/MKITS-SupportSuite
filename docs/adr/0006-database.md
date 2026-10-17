# ADR-0006: SQLite as database

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

The server needs persistent storage for users, devices, sessions, bans and audit logs. Expected scale is small to medium.

## Decision

**SQLite** via EF Core in WAL mode, stored on the Docker data volume. Data access behind repository interfaces.

## Consequences

- No separate database server; backup = copy the volume (with SQLite online backup).
- Write concurrency is limited; revisit (PostgreSQL) if scale grows.
