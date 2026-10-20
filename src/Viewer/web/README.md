# Browser viewer (`src/Viewer/web`)

The supporter's side of a session in the browser. TypeScript, no framework; protocol code generated from
`proto/` with Protobuf-ES (ADR-0017); end-to-end crypto with WebCrypto.

| File | Purpose |
|---|---|
| `src/crypto.ts` | ECDH P-256 → HKDF-SHA256 → AES-256-GCM, replay protection, security code – byte-compatible with `Common/Crypto/SessionCrypto.cs` |
| `src/session.ts` | `ViewerSession`: WebSocket handshake with the one-time ticket, key exchange on every `ACTIVE`, encrypted send/receive in order |
| `src/display.ts` | canvas rendering (JPEG), frame acks, pointer/keyboard → input payloads (letterbox-aware) |
| `src/main.ts` | wiring for the page `/sessions/{id}/view` (rendered by the server: `Features/Relay/ViewerPage.cs`) |
| `test/` | vitest tests; `generate-test-vector.mjs` created `proto/testvectors/e2e-v0.json` (checked by C# too) |

```bash
npm ci
npm test         # generates src/gen, runs vitest
npm run build    # type check + bundle → src/Server/MKITS.SupportSuite.Server/wwwroot/viewer/viewer.js
```

Rules: never use `innerHTML` with remote data (chat, monitor names); no inline scripts (CSP `script-src 'self'`);
all texts come from the server (`SharedResource` EN/DE) via the JSON block `#viewer-texts`.
