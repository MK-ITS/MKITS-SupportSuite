# End-to-end tests (15.2)

Playwright tests of the web UI against a **running server with an empty database**. They walk through what an
operator does first: the first-run setup, the mandatory two-factor setup of the admin, creating a session, the
customer entering the code in the portal (and a wrong code), the viewer waiting for the agent, the admin pages, the
security headers and the language selection.

The relay itself – agents, viewers, consent, encryption, file transfer, shells – is covered by the end-to-end tests
in `tests/MKITS.SupportSuite.Server.Tests` (real WebSocket clients against the test server).

## Run

CI runs them in the job *End-to-end tests (web UI)*. Locally:

```bash
# 1. Server with a fresh data folder (the setup token is printed to the log)
cd src/Viewer/web && npm ci && npm run build && cd -
dotnet publish src/Server/MKITS.SupportSuite.Server -c Release -o publish/server
cd publish/server
Kestrel__Endpoints__Http__Url=http://127.0.0.1:5080 Mkits__PublicBaseUrl=http://127.0.0.1:5080 \
  Mkits__DataDirectory=/tmp/mkits-e2e dotnet MKITS.SupportSuite.Server.dll

# 2. Tests (another terminal), with the token from the server log
cd tests/e2e && npm ci && npx playwright install chromium
E2E_SETUP_TOKEN=XXXX-XXXX-… npx playwright test
```

The specs build on each other (`01-setup` creates the admin and stores its TOTP key in `.e2e-state.json`), so they
run in order in one worker. Start again with an empty data folder.
