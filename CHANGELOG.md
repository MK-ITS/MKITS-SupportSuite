# Changelog

All notable changes to MKITS-SupportSuite are listed here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), versions follow [Semantic Versioning](https://semver.org/).
How a release is made: [docs/releasing.md](docs/releasing.md).

## [Unreleased]

## [0.1.0] - unreleased

First release: self-hosted server with browser viewer, Windows and Linux clients.

### Server

- Docker image (`ghcr.io/mk-its/mkits-supportsuite-server`) with SQLite, first-run setup with a one-time token,
  nginx example with TLS, rate limits and WebSocket settings.
- Users and roles (Admin, Supporter), ASP.NET Core Identity, TOTP two-factor authentication (mandatory for Admins),
  recovery codes, IP bans after failed attempts, in-app rate limits.
- Ad-hoc sessions with single-use 8-digit codes (10 minutes), customer portal with client download, several supporters
  per session.
- Installed devices: enrollment tokens, device groups with permissions and consent policies, online status, update
  distribution.
- Relay with end-to-end encryption (ECDH P-256, HKDF, AES-256-GCM), security code for comparison, per-channel
  permission checks.
- Tamper-evident audit log, health endpoints, Prometheus metrics (`/metrics`).
- Web UI and all texts in English and German.

### Browser viewer

- Screen with H.264 (WebCodecs) or JPEG, monitor selection, picture quality, 1:1 view, mouse and keyboard, special
  keys.
- Chat, file transfer both ways, clipboard, terminals (user and admin).

### Windows client and agent

- Single EXE for ad-hoc sessions (no installation) and as installed service.
- Screen capture with DXGI Desktop Duplication (GDI fallback), H.264 via Media Foundation, secure desktop and lock
  screen in administrator mode or as service, Ctrl+Alt+Del.
- PowerShell and SYSTEM terminals, files, clipboard; signed automatic updates with rollback.

### Linux client and agent

- AppImage for ad-hoc sessions and Debian package for the installed agent (x86_64 and arm64).
- X11 and Wayland (portal) capture, input, terminals (user and root), files, clipboard; signed updates.

### Known limitations

- No Android agent, native viewer or session recording yet.
- The Windows EXE is not Authenticode-signed unless the operator signs it (SmartScreen warns, ADR-0009).
- No external penetration test yet ([security review](https://github.com/MK-ITS/MKITS-SupportSuite/blob/main/docs/security-review.md)).

[Unreleased]: https://github.com/MK-ITS/MKITS-SupportSuite/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/MK-ITS/MKITS-SupportSuite/releases/tag/v0.1.0
