# ADR-0014: Consent policy

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Remote access must be transparent to end users (privacy, GDPR, works councils).

## Decision

- **Ad-hoc:** customer approval is always required; a visible banner is shown during the session.
- **Permanent:** configurable per device group: *ask*, *notify only* or *silent*.

## Consequences

- Consent decisions are written to the audit log.
