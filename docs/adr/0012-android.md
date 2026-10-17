# ADR-0012: Android: accept OS limitations, native Kotlin app, APK from own server

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Android 10+ requires user consent for screen capture (MediaProjection) and an Accessibility Service for input injection. True unattended control needs device-owner/MDM or root.

## Decision

Accept these limitations. Native **Kotlin** app, distributed as **signed APK from the own server** (no Play Store for now).

## Consequences

- Each session needs consent on the device.
- Users must enable the Accessibility Service once.
