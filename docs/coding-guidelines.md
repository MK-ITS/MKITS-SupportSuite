# Coding guidelines

Goal: code that a human or an AI agent can understand quickly and change safely.
Most C# rules are enforced automatically by `.editorconfig` + analyzers (warnings are errors).

## General

- English for all identifiers, comments, logs and docs.
- Prefer clarity over brevity. Name things after what they mean in the domain (`SessionCode`, `DeviceGroup`, `IpBan`).
- One responsibility per class; keep files small (guideline: < 300 lines).
- Explain *why* in comments, not *what*. Public types and members get XML documentation (`///`) when their purpose is not obvious.
- No dead code, no commented-out code. Use the issue tracker / WORKPLAN for TODOs; `// TODO(phase-x.y): …` only with a WORKPLAN reference.

## C# / .NET

- File-scoped namespaces matching the folder path.
- `Nullable` is enabled – no `!` null-forgiving operator without a justifying comment.
- Async: suffix `Async`, accept `CancellationToken` as last parameter, never `.Result` / `.Wait()`, `ConfigureAwait(false)` in libraries.
- Dependency injection via constructor (primary constructors are fine). No service locator.
- Configuration: strongly typed options classes (`IOptions<T>`), validated at startup (`ValidateOnStart`).
- Errors: throw specific exceptions for programming errors; return result types for expected failures (e.g. invalid session code).
- Logging: `ILogger<T>`, structured message templates (`"Session {SessionId} started"`), source-generated `LoggerMessage` for hot paths. Never log secrets, passwords, session codes, tokens or key material.
- Time: inject `TimeProvider`; never call `DateTime.Now` directly (testability, UTC everywhere).
- Platform code: only in `Agent.Windows` / `Agent.Linux`, behind interfaces defined in `Agent.Core`. Mark with `[SupportedOSPlatform]` where needed.

### Project structure inside a component

Organize by feature, not by technical layer:

```
src/Server/MKITS.SupportSuite.Server/
  Endpoints/          minimal API endpoint groups
  Features/
    IpBans/           IpBanService.cs, IpBanOptions.cs, IpBanEndpoints.cs, …
    Sessions/
  Infrastructure/     database, localization, reverse-proxy setup
  Resources/          localized UI strings (*.resx, en + de)
```

## Localization

- UI strings in resource files: `Strings.resx` (English, default) + `Strings.de.resx` (German).
- Resource keys are English and descriptive: `Login_Error_InvalidCredentials`.
- The UI culture comes from the user profile; fallback: browser `Accept-Language`, then English.

## Security rules

- Validate every external input (size, format, range) at the boundary.
- Authorization checks on the server for every action – never rely on the UI hiding a button.
- Use framework crypto only (`System.Security.Cryptography`, WebCrypto); no custom algorithms.
- Compare secrets in constant time (`CryptographicOperations.FixedTimeEquals`).
- Use cryptographically secure randomness for codes and tokens (`RandomNumberGenerator`).

## Tests

- xUnit; one test class per class under test; names `Method_Scenario_ExpectedResult`.
- Unit tests must not touch network, disk or the clock (use fakes, `FakeTimeProvider`).
- Integration tests for endpoints via `WebApplicationFactory<Program>`.
- Security-relevant code (auth, bans, consent, crypto, session codes) requires tests for the negative cases.

## TypeScript (browser viewer) and Kotlin (Android)

Rules will be added when these components start (Phases 9 and 12). Same principles: strict typing, no warnings, English, localized UI.
