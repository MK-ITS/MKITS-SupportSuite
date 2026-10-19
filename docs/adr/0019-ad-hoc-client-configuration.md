# ADR-0019: Ad-hoc client receives server address and code via file name

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

The ad-hoc client must know which server to contact and which session code to use. It is downloaded from the portal by non-technical users. Binaries will be code-signed (ADR-0009), so they cannot be modified per download. MKITS is self-hosted, so the server address differs per operator.

## Options considered

1. **Embed data in the signed binary** (Authenticode unauthenticated attributes, as some commercial tools do) – no user interaction, but complex and fragile.
2. **Encode server host and code in the download file name** – simple, works with a signed generic binary; file names can be altered by users or browsers.
3. **User types server address and code** – always works, error-prone.

## Decision

Option 2 with option 3 as fallback: `MKITS-Support_<host>_<code>.exe`. The client tolerates browser suffixes such as ` (1)`, validates host (DNS name, optional port) and code format, and falls back to asking. The consent dialog always shows the server host and operator name.

Option 1 may be added later as an improvement without changing the protocol.

## Consequences

- One generic signed binary per platform, published with each release.
- A renamed file could point to another server – no new risk, since an attacker could hand out any binary anyway; the visible host in the consent dialog mitigates confusion.
