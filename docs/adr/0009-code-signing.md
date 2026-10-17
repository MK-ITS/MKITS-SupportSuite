# ADR-0009: Code signing deferred

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Unsigned remote tools trigger SmartScreen and antivirus warnings.

## Decision

Develop unsigned. Obtain a code-signing certificate (e.g. Azure Trusted Signing) **before production use**.

## Consequences

- The build pipeline must have a signing step that can be enabled later.
- Agent auto-update must verify signatures independently of Authenticode.
