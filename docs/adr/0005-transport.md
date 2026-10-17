# ADR-0005: Relay-only transport via WebSocket Secure on port 443

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Requirement: all communication over HTTPS 443 if possible. P2P (WebRTC) would need STUN/TURN and UDP ports.

## Decision

All traffic between agents and viewers is **relayed by the server** over **WSS on port 443** through the reverse proxy. No P2P.

## Consequences

- Works through NAT and restrictive firewalls without extra ports.
- Server bandwidth grows with active sessions (~1–5 Mbit/s each).
- nginx must be configured for WebSocket upgrades and long timeouts.
