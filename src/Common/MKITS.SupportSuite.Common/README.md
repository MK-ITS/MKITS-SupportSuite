# MKITS.SupportSuite.Common

Code shared by the server, the agents and the native viewer. No dependencies on ASP.NET Core or a platform.

| Folder | Content |
|---|---|
| `Protocol` | C# types generated from [`/proto`](../../../proto) at build time (ADR-0017), `FrameCodec` (size-limited framing), `PayloadChannels` (which payload belongs to which channel), `ChannelPermissions` (permission per channel, wire names), `ErrorCodes`, `ProtocolVersion` |
| `Crypto` | `SessionCrypto`: ECDH P-256 key agreement, HKDF, AES-256-GCM per channel and direction with replay protection, security code (ADR-0018); checked against the shared test vector `proto/testvectors/e2e-v0.json`, which the browser viewer uses too |
| `Sessions` | session code format, ad-hoc client file names (code and host in the name) |
| `ProductInfo.cs` | product name and version |

Wire format and channel rules: [docs/protocol.md](../../../docs/protocol.md). Changing the protocol: [docs/extending.md](../../../docs/extending.md#a-session-feature-new-payload-between-viewer-and-agent).
