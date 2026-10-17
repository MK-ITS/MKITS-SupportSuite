# ADR-0013: Permanent devices organized in groups with per-group permissions

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Admins set up permanent connections; Users act as supporters.

## Decision

Devices belong to **groups** (e.g. per customer). **Users get access per group**; Admins see everything.

## Consequences

- Authorization checks on every device access.
- Enrollment tokens and installer packages are created per group.
