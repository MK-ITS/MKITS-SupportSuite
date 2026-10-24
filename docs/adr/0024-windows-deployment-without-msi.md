# ADR-0024: Windows deployment without an MSI package

- **Status:** Accepted
- **Date:** 2026-10-25

## Context

Phase 10.2 planned an MSI package (WiX) for mass deployment via GPO/Intune. Since WiX v6 the toolset requires an
"Open Source Maintenance Fee" from organizations using it; WiX v5 is free (MS-RL) but no longer the maintained line.
The agent already installs itself with one command (`MKITS-Agent.exe --install --server … --token …`, ADR-0022), runs
unattended, reports through exit codes and keeps the identity on re-installation.

## Decision

No MSI package. The single EXE is the deployment unit:

- **Intune:** Win32 app (`.intunewin` built from the EXE) with install/uninstall commands and a file or service
  detection rule.
- **GPO / software deployment / RMM:** computer startup script or job that runs the install command once (guarded by
  a check for the service).
- Exit codes are documented (0 ok, 2 arguments, 5 not elevated, 6 service, 7 files).

## Consequences

- No licensing costs or toolchain for MSI; one artifact for ad-hoc and permanent use.
- Tools that only accept MSI need a wrapper (e.g. the RMM's own packaging). An MSI can still be added later if a
  concrete requirement appears – this ADR would then be superseded.
- Enrollment tokens end up in deployment configurations (Intune, SYSVOL). Recommended: short lifetime, limited number
  of devices, "new devices need approval" for broad rollouts, revoke after the rollout.
