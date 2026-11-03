# Browser viewer (`src/Viewer/web`)

The supporter's side of a session in the browser. TypeScript, no framework; protocol code generated from
`proto/` with Protobuf-ES (ADR-0017); end-to-end crypto with WebCrypto.

| File | Purpose |
|---|---|
| `src/crypto.ts` | ECDH P-256 → HKDF-SHA256 → AES-256-GCM, replay protection, security code – byte-compatible with `Common/Crypto/SessionCrypto.cs` |
| `src/session.ts` | `ViewerSession`: WebSocket handshake with the one-time ticket, key exchange on every `ACTIVE`, encrypted send/receive in order |
| `src/display.ts` | canvas rendering (JPEG, H.264 via `video.ts`), codec announcement, quality presets (`qualityFor`), frame acks, pointer/keyboard → input payloads (letterbox-aware) |
| `src/video.ts` | WebCodecs H.264: codec string from the SPS, decoder lifecycle, gap/decoder-error handling with keyframe requests (ADR-0027) |
| `src/main.ts` | wiring for the page `/sessions/{id}/view` (rendered by the server: `Features/Relay/ViewerPage.cs`); quality and 1:1 view are remembered in `localStorage` (best effort) |
| `test/` | vitest tests; `generate-test-vector.mjs` created `proto/testvectors/e2e-v0.json` (checked by C# too) |

```bash
npm ci
npm test         # generates src/gen, runs vitest
npm run build    # type check + bundle → src/Server/MKITS.SupportSuite.Server/wwwroot/viewer/viewer.js
```

Rules: never use `innerHTML` with remote data (chat, monitor names); no inline scripts (CSP `script-src 'self'`);
all texts come from the server (`SharedResource` EN/DE) – rendered into the page or via the JSON block `#viewer-texts`.
