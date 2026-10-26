# ADR-0023: Signed agent auto-update

- **Status:** Accepted
- **Date:** 2026-10-24

## Context

Installed agents must be kept up to date without visiting every device (ADR-0015). An update replaces a program
that runs as LocalSystem, so whoever can publish updates controls every device. The server must not have that power:
end-to-end encryption (ADR-0018) is designed so that a compromised server cannot take over sessions, and an update
channel controlled by the server would undo that. Code signing (Authenticode) is not available yet (ADR-0009).

## Decision

- **Release key.** An ECDSA P-256 key pair. The **private key exists only as the CI secret
  `AGENT_UPDATE_SIGNING_KEY`**; the **public key is compiled into the agent** (`deploy/keys/agent-update.pub`,
  `ProductInfo.AgentUpdatePublicKey`). Builds without that file cannot update themselves.
- **Build number** orders builds: the commit timestamp of the image build (`ProductInfo.BuildNumber`). Local builds
  have 0 and are neither offered nor updated.
- **Manifest** `{product, platform, build, version, sha256, size}` signed over its exact bytes (P1363) by the image
  build (BuildKit secret, never stored in a layer) and placed next to the EXE (`MKITS-Support.exe.manifest.json/.sig`).
- **Server** only serves manifest, signature and file (`GET /api/agent-updates/{channel}`, `…/file`; channels `windows-x64` and `linux-x64-deb`, ADR-0025). It does not
  verify anything – agents do. An operator-provided EXE without matching manifest disables updates for that platform.
- **Agent** (Windows service) checks 5 minutes after start and then every 6 hours (with jitter). It accepts only a
  manifest signed by its compiled-in key, for its platform, with a **higher build number** (no downgrades), and only
  while **no session is running**. The download must match the signed size and SHA-256. The verified copy in the
  protected data folder replaces the installed EXE (`--apply-update`); if the service does not keep running, the
  previous EXE is restored.

## Consequences

- A compromised server can withhold updates but cannot install anything.
- The release key is a critical secret: losing it stops updates (agents must be reinstalled with a new key); leaking it
  allows malicious updates to every agent that trusts it – rotate by publishing a new public key and reinstalling.
- Agents installed before the public key was added have no key and must be updated once manually (`--install`).
- Self-hosters who build their own images use their own key pair (same file and secret names) or run without updates.
- Authenticode signing (ADR-0009) is added on top later; it does not replace this check.
