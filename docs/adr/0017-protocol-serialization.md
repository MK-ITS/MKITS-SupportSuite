# ADR-0017: Protocol Buffers for the wire protocol

- **Status:** Accepted
- **Date:** 2026-10-17

## Context

Server and agents are C#, the Android agent is Kotlin and the browser viewer is TypeScript (ADR-0002). All must speak the same binary protocol over WebSockets, and the protocol must evolve without breaking older peers.

## Options considered

1. **Protocol Buffers (proto3)** – schema-first, code generation for all three languages, compact, forward/backward compatible (unknown fields ignored). License BSD-3-Clause.
2. **MessagePack** – compact, but schema lives in code per language; drift between implementations is likely.
3. **JSON** – readable, but large for video/input traffic and no schema enforcement.

## Decision

Use **Protocol Buffers (proto3)**. The schema in `proto/mkits/v0/` is the single source of truth; generated code is not committed by hand-editing. Package `mkits.v0` matches the protocol major version.

## Consequences

- Code generation: C# via `Grpc.Tools`/`Google.Protobuf`, TypeScript via `@bufbuild/protobuf`, Kotlin via `protobuf-kotlin-lite`.
- A breaking change requires a new package (`mkits.v1`) and a protocol major bump.
- Field numbers are never reused.
