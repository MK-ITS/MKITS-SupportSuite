# ADR-0010: Server delivered as Docker / Compose

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

The server runs on Ubuntu 26.04+.

## Decision

The server is shipped as a **Docker image** with a **Compose** file (server + optional nginx). Data on a volume mounted at `/data`.

## Consequences

- Image published to GHCR (Phase 14).
- Installation guide covers Docker, nginx and TLS (Let's Encrypt).
