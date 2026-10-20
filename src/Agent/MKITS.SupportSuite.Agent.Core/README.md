# MKITS.SupportSuite.Agent.Core

Platform-independent agent logic. Platform projects (Windows, Linux) and later the Android port provide the UI and
the device integrations; everything protocol-related lives here.

| Folder / type | Purpose |
|---|---|
| `Connection/IWebSocketConnector` | opens the WebSocket (`ClientWebSocketConnector` in production, a test-server connector in tests) |
| `Connection/FrameSocket` | one frame per WebSocket message, serialized sends |
| `Sessions/AgentSession` | handshake, authentication (code / resume), consent, E2E key exchange per supporter, decrypt + dispatch, reconnect |
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

Unit tests: `tests/MKITS.SupportSuite.Agent.Core.Tests`. End-to-end tests: `tests/MKITS.SupportSuite.Server.Tests/Features/AgentEndToEndTests.cs`.
