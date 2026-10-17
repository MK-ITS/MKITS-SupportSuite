# Architecture Decision Records

Each significant decision is recorded as one file `NNNN-short-title.md` (copy [template.md](template.md)).
ADRs are never edited after acceptance except for status changes; a new ADR supersedes an old one.

Statuses: Proposed · Accepted · Superseded by ADR-XXXX · Deprecated

| ADR | Title | Status |
|---|---|---|
| [ADR-0000](0000-license-and-languages.md) | License AGPL-3.0, English internally, UI in English and German | Accepted |
| [ADR-0001](0001-session-types.md) | Graphical console sessions and remote shell | Accepted |
| [ADR-0002](0002-technology-stack.md) | .NET 10 / C# as main stack, Kotlin for Android | Accepted |
| [ADR-0003](0003-viewer.md) | Browser viewer first, native desktop viewer later | Accepted |
| [ADR-0004](0004-ad-hoc-flow.md) | Supporter creates the ad-hoc session code | Accepted |
| [ADR-0005](0005-transport.md) | Relay-only transport via WebSocket Secure on port 443 | Accepted |
| [ADR-0006](0006-database.md) | SQLite as database | Accepted |
| [ADR-0007](0007-mvp-scope.md) | MVP feature scope | Accepted |
| [ADR-0008](0008-authentication.md) | Local accounts with TOTP two-factor authentication | Accepted |
| [ADR-0009](0009-code-signing.md) | Code signing deferred | Accepted |
| [ADR-0010](0010-server-deployment.md) | Server delivered as Docker / Compose | Accepted |
| [ADR-0011](0011-linux-display-servers.md) | Linux agent supports X11 and Wayland from the start | Accepted |
| [ADR-0012](0012-android.md) | Android: accept OS limitations, native Kotlin app, APK from own server | Accepted |
| [ADR-0013](0013-device-groups.md) | Permanent devices organized in groups with per-group permissions | Accepted |
| [ADR-0014](0014-consent.md) | Consent policy | Accepted |
| [ADR-0015](0015-security-features.md) | Planned security features | Accepted |
| [ADR-0016](0016-repository.md) | Public GitHub repository with GitHub Actions | Accepted |
