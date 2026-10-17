# ADR-0004: Supporter creates the ad-hoc session code

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Ad-hoc support starts with a code. Either the supporter or the customer could create it.

## Decision

The **supporter creates** the session in the web UI and tells the customer the code. The customer enters it on the portal, downloads the generic client and approves the connection.

## Consequences

- Codes must be short-lived, single-use and protected against guessing (rate limiting + IP bans).
- The client binary stays generic and signed; the code is passed via file name or typed in.
