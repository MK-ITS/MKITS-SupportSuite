# MKITS.SupportSuite.Agent.Core

Platform-independent agent logic. Platform projects (Windows, Linux) and later the Android port provide the UI and
the device integrations; everything protocol-related lives here.

| Folder / type | Purpose |
|---|---|
| `Connection/IWebSocketConnector` | opens the WebSocket (`ClientWebSocketConnector` in production, a test-server connector in tests) |
| `Connection/FrameSocket` | one frame per WebSocket message, serialized sends |
| `Sessions/AgentSession` | ad-hoc: handshake, authentication with the code, resume after a connection loss |
| `Sessions/SessionEngine` | shared by both modes: viewers and their permissions, consent, E2E key exchange per supporter, decrypt + dispatch |
| `Devices/DeviceAgent` | permanent: enrollment, device-key authentication, idle presence, sessions over the presence connection, reconnect with backoff |
| `Bridge/UserSessionBridge`, `BridgeClient`, `BridgeProtocol` | split agent: the service keeps connection and keys, a process in the user's session runs chat/clipboard/files/shell with the user's rights |
| `Devices/DeviceIdentity`, `IDeviceIdentityStore`, `FileDeviceIdentityStore` | device id + ECDSA P-256 key; the platform protects the private key (DPAPI / file mode 0600) |
| `Sessions/IAgentUserInterface` | implemented by the platform UI: consent dialog, status/banner, supporters, security code |
| `Sessions/ISessionChannelHandler` | implemented per feature; receives decrypted payloads, `OnViewerReadyAsync` after the key exchange, `OnViewerGone` |
| `Features/ScreenStreamer` + `IScreenSource` | screen streaming with flow control (≤ 3 frames in flight), change detection, per-viewer fps/quality/monitor |
| `Features/InputHandler` + `IInputInjector` | normalized coordinates → desktop pixels, button transitions, stuck-key release |
| `Features/ClipboardHandler` + `IClipboard` | text both ways, no echo, current content is not pushed to a new supporter |
| `Features/ChatHandler` + `IChatPresenter` | chat with length limits; local arrival time is displayed |

Rules:

- No platform APIs here (no P/Invoke, no Windows/Linux packages).
- Everything from the network is hostile: payloads are only accepted after decryption, replay check and a channel/payload match (`PayloadChannels.Matches`).
- The consent dialog never blocks the receive loop; the server enforces the timeout.
- Viewer permissions (`ViewerJoined.permissions`) are enforced here as well as on the relay, in both directions (`ChannelPermissions`).

Unit tests: `tests/MKITS.SupportSuite.Agent.Core.Tests`. End-to-end tests: `tests/MKITS.SupportSuite.Server.Tests/Features/AgentEndToEndTests.cs`.
