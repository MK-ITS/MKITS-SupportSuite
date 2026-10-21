# Security concept

> Status: **specification (Phase 2)**. This document is the reference for security reviews (Phase 15).
> MKITS-SupportSuite grants remote control over computers – every design decision assumes hostile network input.

## 1. Assets

| Asset | Why it matters |
|---|---|
| Control over end devices | full compromise of customer machines |
| Session content (screen, keystrokes, files, shell) | confidential customer data |
| User accounts (especially Admins) | can enroll devices, read audit logs, change policies |
| Device keys, enrollment tokens | impersonation of devices, rogue device enrollment |
| Server keys (Data Protection, update signing, recording key) | forging cookies, distributing malicious updates, reading recordings |
| Audit log | accountability; must not be silently altered |

## 2. Actors and trust boundaries

```
 Internet (untrusted) │ Reverse proxy (trusted for X-Forwarded-*) │ Server process (trusted) │ /data volume (trusted, protect!)
 ────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
  Guests, customers,    nginx: TLS, request limits                   authN/authZ, broker,       SQLite, keys, recordings
  supporters, agents,                                                relay
  attackers
```

- The server trusts `X-Forwarded-For/Proto` **only** from configured proxy addresses (`Mkits:ReverseProxy:KnownProxies/KnownNetworks`). Otherwise the client IP would be spoofable and the IP ban service useless.
- End users trust the agent they run; the agent trusts the server's TLS certificate (system trust store, optional pinning for permanent agents).
- Session content is protected end-to-end (ADR-0018): a compromised server can deny service and see metadata, but reading content requires an active MITM, which the SAS fingerprint and device-key signatures reveal.

## 3. Threats and mitigations

| # | Threat | Mitigation |
|---|---|---|
| T1 | Password guessing / credential stuffing | IP ban service (5 failures in 10 min → 7-day ban, allow/deny lists), ASP.NET Core Identity lockout, PBKDF2 password hashing (Identity v3 hasher), password policy, mandatory TOTP for Admins (optional-enforceable for Users) |
| T2 | Session code guessing | 8-digit codes, 10-min validity, single use, hashed storage, failures counted by the IP ban service, rate limiting on code endpoints |
| T3 | Client IP spoofing via headers | forwarded headers accepted only from known proxies; forward limit 1 by default |
| T4 | Session hijacking (cookies) | `__Host-` cookies, `Secure`, `HttpOnly`, `SameSite=Strict`, short sliding expiration, re-authentication for sensitive admin actions, cookie invalidation on password/2FA change |
| T5 | CSRF / clickjacking / XSS | antiforgery tokens on all state-changing forms, `Content-Security-Policy` without `unsafe-inline` scripts, `X-Frame-Options: DENY` / `frame-ancestors 'none'`, output encoding by Razor; the viewer page additionally allows inline *styles* (xterm.js), never inline scripts |
| T6 | Unauthorized device access | per-group permissions checked server-side for every action (view, control, files, shell); viewer tickets are single-use, 60-s, bound to user + session |
| T7 | Rogue agent enrollment | enrollment tokens: random 256 bit, hashed, expiry + max uses, revocable; enrolled devices must be approved by an Admin if the group requires it |
| T8 | Device impersonation | device authenticates by ECDSA P-256 signature over a server nonce; private key stored with OS protection (Windows: DPAPI machine scope / CNG; Linux: root-only file 0600) |
| T9 | Server reads session content | E2E encryption (ECDH P-256 + HKDF + AES-256-GCM), SAS fingerprint, device-key signature on ephemeral keys; exception: recorded sessions, announced to all participants (ADR-0018) |
| T10 | Replay / reordering on the session plane | GCM nonce from per-direction sequence numbers, strictly increasing; rekey limits |
| T11 | Malicious update to agents | updates signed with an offline-generated ECDSA P-256 update key; public key compiled into the agent; agent verifies before installing; downgrade protection by version check |
| T12 | Abuse by scammers ("tech support scam") | consent dialog naming operator and supporter, permanent on-screen banner, *End session* always available, no hidden mode for ad-hoc, server address shown in client |
| T13 | Privilege escalation through the agent | Windows service ↔ helper IPC via named pipe with SYSTEM-only ACL; UI process unprivileged; no remote command reaches the service without an authenticated, consented session; shell `elevated` requires the session to run elevated and the permission `shell` |
| T14 | Path traversal / malicious files in transfers | receiver sanitizes names (no paths, reserved names, length), writes to the user's download folder, never executes; size limits; SHA-256 verification |
| T15 | Denial of service | nginx connection/request limits, server message size limits, per-IP connection caps, video backpressure, bounded queues |
| T16 | Audit log tampering | append-only table, each entry contains `SHA-256(previous_hash ‖ entry)`; integrity check in the admin UI; export for external archiving |
| T17 | Secrets in logs | structured logging with explicit fields; codes, tickets, tokens, passwords and keys are never logged; IPs are logged (needed for bans) with configurable retention |
| T18 | Supply chain | central package versions, Dependabot, CodeQL, `NU190x` vulnerability audit fails the build, reproducible Docker build, license check |

## 4. Authentication details

- **Accounts:** ASP.NET Core Identity on SQLite. Roles: `User`, `Admin` (`Guest` = unauthenticated, not stored).
- **First run:** if no Admin exists, the server prints a one-time **setup token** to the log; `/setup` requires it to create the first Admin (prevents takeover of fresh installs).
- **TOTP (RFC 6238):** 30-s steps, ±1 step tolerance, 10 single-use recovery codes (hashed). Mandatory for Admins; a setting can make it mandatory for Users.
- **Failed attempts** of all kinds (password, TOTP, session code, enrollment token, viewer ticket, device signature) feed the IP ban service with a `kind` tag.

## 5. IP ban service

- Sliding window per IP: default **5 failures within 10 minutes → ban for 7 days** (all configurable).
- **Allow list** (CIDR): never banned (e.g. office network). **Deny list** (CIDR): always rejected.
- Bans are enforced as the first middleware (before authentication) and on WebSocket upgrade; banned IPs receive `403` without details.
- Bans persist in SQLite; counters in memory. Admins can view, create and lift bans; every ban/unban is audited; optional e-mail notification to Admins (SMTP).
- IPv6: counted and banned per **/64** by default (configurable), as single addresses are cheap to rotate.

## 6. Key management

| Key | Where | Rotation |
|---|---|---|
| Data Protection keys (cookies, antiforgery, tickets) | `/data/keys`, encrypted at rest with a certificate if configured | automatic (90 days) |
| Device key pair | on the device | on re-enrollment |
| Ephemeral session keys | memory only | per session / rekey |
| Update signing key | **offline**, never on the server | manual, key id in signature |
| Recording key | `/data/keys/recording` | configurable; old keys kept for playback |

## 7. Platform security

- **Windows:** respects UAC – remote elevation always requires a UAC prompt answered on the device (ad-hoc) or a service installed by an Admin (permanent). The secure desktop is only accessible from the SYSTEM helper. Binaries will be Authenticode-signed before production (ADR-0009).
- **Linux:** root service + per-session user helper; Wayland access only through xdg-desktop-portal (user consent); no `setuid` binaries.
- **Android:** MediaProjection consent per session, Accessibility Service enabled by the user (ADR-0012); the app shows a persistent notification during sessions.

## 8. Privacy

- Data minimization: only metadata needed for operation and audit is stored.
- Retention defaults: audit log 365 days, recordings 30 days, failed-attempt records 30 days – all configurable.
- Recording is visible to all participants before and during the session.
- The operator is the data controller; the admin guide lists what is stored (see [guides](guides/en/administration.md)).
