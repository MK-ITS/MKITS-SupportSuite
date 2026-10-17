# ADR-0008: Local accounts with TOTP two-factor authentication

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Users and Admins need to authenticate. Options were local accounts, TOTP, OIDC and LDAP.

## Decision

**Local accounts** (ASP.NET Core Identity) with **TOTP 2FA**. No OIDC/LDAP for now.

## Consequences

- Recovery codes are required.
- External identity providers can be added later without changing the role model.
