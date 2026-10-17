# ADR-0003: Browser viewer first, native desktop viewer later

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Supporters need to connect to sessions. A browser viewer needs no installation; a native viewer can be more capable.

## Decision

Both. The **browser viewer** (HTML5 canvas, WebCodecs, WebCrypto) is part of milestone M1. A **native desktop viewer** (C#, Avalonia) follows later and uses the same protocol.

## Consequences

- The protocol must be implementable in browsers (WebSocket, WebCodecs-compatible H.264).
- Feature parity must be maintained between both viewers.
