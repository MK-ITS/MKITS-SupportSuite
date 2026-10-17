# ADR-0016: Public GitHub repository with GitHub Actions

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Code hosting and CI are needed.

## Decision

**Public** repository `MK-ITS/MKITS-SupportSuite` on GitHub, CI with **GitHub Actions**, CodeQL and Dependabot.

## Consequences

- No secrets in the repository; `SECURITY.md` with private vulnerability reporting.
- Branch protection on `main` is recommended.
