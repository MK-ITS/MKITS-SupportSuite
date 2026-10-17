# ADR-0002: .NET 10 / C# as main stack, Kotlin for Android

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

The suite needs a web server, privileged Windows components, Linux agents and an Android app. The code must be readable and maintainable.

## Decision

- **.NET 10 LTS / C#** for the server (ASP.NET Core), Windows and Linux agents and the native viewer.
- **Kotlin** for the Android agent (direct access to MediaProjection, MediaCodec, AccessibilityService).
- **TypeScript** for the browser viewer.

## Consequences

- One language for most of the code, strong typing, built-in localization.
- Protocol definitions must be shared across C#, Kotlin and TypeScript (schema-based, e.g. Protobuf).
- .NET 10 is supported until November 2028; plan the move to the next LTS.
