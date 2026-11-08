# Security review (15.3)

A code-level review of the whole system before the first release, structured along the
[OWASP Application Security Verification Standard (ASVS) 4.0.3](https://owasp.org/www-project-application-security-verification-standard/)
chapters that apply to this product. It complements the threat model in [security-concept.md](security-concept.md).

- **Date:** 2026-11-07 · **Scope:** `main` at the state of PR #62 – server, relay, browser viewer, agent core,
  Windows and Linux agents, deployment files, CI.
- **Method:** reading the code paths that handle input from the network or from less privileged processes, the
  configuration, and the dependency set; automated checks that run on every PR (analyzers, CodeQL, NuGet audit,
  tests). **Not** a penetration test: an external test of the portal, the relay and the end-to-end encryption is
  still recommended before production use (see *Open items*).

## Findings

| # | Severity | Finding | Status |
|---|---|---|---|
| F1 | Medium | GitHub Actions were referenced by mutable major tags (`@v7`). A compromised or re-tagged action would run with the workflow's token – in `publish.yml` with permission to push the server image. | **Fixed:** all actions pinned to commit SHAs (version in a comment); Dependabot (`github-actions`) keeps the pins current. |
| F2 | Medium (availability) | nginx limited every client address to 50 concurrent connections, the agent WebSockets included: more than 50 installed agents behind one NAT address (a normal company network) could not all connect. | **Fixed:** `/ws/agent` allows 2000 connections per address in a zone of its own; browsers (pages and `/ws/viewer`) keep 50 and are not crowded out by agents behind the same address. |
| F3 | Low | Rate limits existed only in the nginx example; other reverse proxies left only the counting of *failed* attempts. | **Fixed (PR #62):** in-app limits per client network (`Mkits:RateLimits`). |
| F4 | Medium | The AppImage build downloaded `appimagetool` and the AppImage runtime from moving `continuous` releases without any check – whatever was published there ended up in the customer client. (Found after the review, when those downloads failed in CI.) | **Fixed (PR #71):** pinned versions, SHA-256 verified, retried downloads. |

No finding of severity *high* or *critical* was found.

## Verified areas

| ASVS chapter | What was checked | Result |
|---|---|---|
| V1 Architecture | Trust boundaries (browser, relay, agents, SYSTEM/root helpers), E2E encryption keeps the relay out of session contents (ADR-0005), ADRs for every security-relevant decision | ✓ |
| V2 Authentication | ASP.NET Core Identity password hashing, TOTP 2FA (mandatory for Admins), recovery codes, first-run setup only with a one-time token from the log (constant-time comparison, serialized, consumed on success), failures counted towards IP bans | ✓ |
| V3 Session management | Cookies `HttpOnly`, `SameSite=Strict`, `Secure` behind TLS, 8 h sliding lifetime, security stamp revalidation (sign-out within a minute after password/2FA reset or disabling) | ✓ |
| V4 Access control | Role policies on every page/endpoint; device-group permissions checked on the server per action and per channel on the relay; `shell_admin` checked by the agent (ADR-0029); one-time viewer tickets bound to user and session (no cookie on the WebSocket → no cross-site WebSocket hijacking) | ✓ |
| V5 Validation, encoding | Protobuf frames size-limited (1 MiB, control frames smaller) and parsed defensively; HTML output encoded (Blazor, `HtmlEncoder` in the viewer page, JSON text block escaped); the viewer renders remote text only as text nodes; no raw SQL apart from a constant `PRAGMA`; file names from the network sanitized (no paths, device names, control characters), files never overwritten | ✓ |
| V6 Cryptography | ECDH P-256 + HKDF + AES-256-GCM per viewer and direction, sequence numbers against replay, security code for out-of-band comparison, device-signed ephemeral keys verified by the viewer, ECDSA device identities, hashed session codes and enrollment tokens, `RandomNumberGenerator` everywhere | ✓ |
| V7 Errors, logging | No secrets in logs (agent core: never tokens, codes, keys, payloads – tested for the enrollment token; the setup token is the documented exception), tamper-evident audit log with hash chain, generic error pages | ✓ |
| V8 Data protection | Agent identities DPAPI-protected (Windows) / root-only files (Linux), data folders ACL-restricted, the enrollment token deleted after enrollment, no caching of the viewer page (`no-store`) | ✓ |
| V9 Communication | TLS 1.2/1.3 and HSTS in the nginx example, agents only `wss://` except an explicit development `--server http://…`, forwarded headers only from configured proxies | ✓ |
| V10 Malicious code, supply chain | Agent updates only with a release-key-signed manifest and hash check, rollback (ADR-0023); dependencies pinned centrally, licenses checked (`THIRD-PARTY-NOTICES.md`), NuGet audit fails the build on known vulnerabilities, `npm audit`: 0 findings; Actions pinned (F1) | ✓ |
| V12 Files | Uploads/downloads only through the E2E file channel with size limits and SHA-256; installer downloads only for Admins; update files public but signed, channels from a fixed list (no paths from the request); the session code reaches the client download only by `POST` (not in logs or history) | ✓ |
| V13 API / WebSocket | Relay authenticates before anything else (code, resume token, device signature, viewer ticket), flow control with drop/disconnect thresholds, viewer limit per session, rate limits on connection attempts | ✓ |
| V14 Configuration | Options validated at startup, security headers incl. strict CSP on every response, server image runs as non-root user, no secrets in the repository (release key created by the owner, docs/development.md) | ✓ |

Local privilege boundaries on the agents were reviewed as well: the SYSTEM helper pipes accept only SYSTEM and the
session's user, verify the client process id and deny network logons; helper requests are validated (monitor ids,
clamped values, string lengths, at most 8 streams, codec checks); the user-session bridge on Linux runs over the
stdio of a process started by the service, not over a socket other users could reach.

## Open items

- **External penetration test** of portal, relay and E2E crypto (recommended before production use; 15.3 stays
  partially open until then).
- **Signed releases:** the release key for agent updates and, if wanted, an Authenticode certificate for the Windows
  EXE are created by the owner (10.4, 16.3); until then Windows SmartScreen warns (ADR-0009).
- **Repository settings** (1.7): branch protection, private vulnerability reporting, secret scanning – owner.
- **IPv6 rate limits** count per `/64` like IP bans; providers handing out larger prefixes per customer can be covered
  with `Mkits:IpBan:IPv6PrefixLength`.
