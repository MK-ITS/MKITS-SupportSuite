# Extending MKITS-SupportSuite

Recipes for the changes that come up most often, with the places that have to change together. Read
[architecture.md](architecture.md) for the big picture and [coding-guidelines.md](coding-guidelines.md) for the rules;
the definition of done is in [AGENTS.md](../AGENTS.md#6-definition-of-done).

## A UI text

Every text the user sees exists in **English and German**.

| Where the text appears | Files |
|---|---|
| Server web UI, viewer page, portal | `src/Server/MKITS.SupportSuite.Server/Resources/SharedResource.resx` and `.de.resx`; use it with `L["Key"]` in Razor or `T("Key")` in `ViewerPage` |
| Browser viewer (texts set from TypeScript) | the same resource files, **plus** the key in the list `ViewerPage` serializes into `#viewer-texts`; read it with `t("Key")` |
| Windows agent | `src/Agent/MKITS.SupportSuite.Agent.Windows/Resources/Strings.resx` and `.de.resx`, via `UiText.Get` |
| Linux agent | `src/Agent/MKITS.SupportSuite.Agent.Linux/Resources/Strings.resx` and `.de.resx`, via `Ui.Get` |

A missing key shows up as the key itself; tests check the permission texts of the agents (`PermissionTextTests`).

## A server feature

1. Folder `src/Server/MKITS.SupportSuite.Server/Features/<Feature>/` (namespace = folder).
2. A `ServiceCollectionExtensions.AddXyz()` and, for HTTP endpoints, a `MapXyz()`; call both from `Program.cs`.
3. Options: a class with `SectionName` (`Mkits:<Feature>`), bound with `.ValidateOnStart()` and an
   `IValidateOptions<T>` that rejects nonsense; document every setting in both administration guides (§ 10).
4. Data: entities and an EF Core migration (see [development.md](development.md#database-migrations-ef-core)); other
   features use the feature's service, never its tables.
5. Pages: Blazor components under `Components/`; admin pages use the `Admin` policy, supporter pages `Supporter`.
6. Security-relevant actions write an audit entry; failures that could be guessing count towards IP bans
   (`IpBanService.RecordFailureAsync` with a fixed kind).
7. Tests in `tests/MKITS.SupportSuite.Server.Tests` – endpoints through `MkitsWebApplicationFactory` (set the client
   address with `TestRemoteIpHeader`), pages that need a browser in `tests/e2e`.

## A session feature (new payload between viewer and agent)

Session payloads are end-to-end encrypted; the relay only sees the channel number.

1. **Protocol** – add the message to `proto/mkits/v0/session.proto` and to the `SessionPayload` `oneof` (new field
   numbers only, never reuse or renumber; ADR-0017). C# is generated at build time (`Common.csproj`), TypeScript with
   `npm run generate` in `src/Viewer/web`. Describe it in [protocol.md](protocol.md).
2. **Channel** – map the payload in `PayloadChannels.For` (`src/Common/.../Protocol/PayloadChannels.cs`). A new
   channel also needs a `Channel` value in `frame.proto` and its permission in `ChannelPermissions.Required`; the relay
   then enforces that permission without knowing the content. The agent drops payloads that arrive on the wrong
   channel.
3. **Agent** – a handler in `Agent.Core/Features` implementing `ISessionChannelHandler` (`HandleAsync`,
   `OnViewerReadyAsync`, `OnViewerGone`); treat every field as hostile (lengths, counts, ids). Platform work goes behind
   an interface next to the handler, implemented in the Windows and Linux projects; register the handler where the
   agents build their handler lists (Windows `MainForm`, `SessionWindow` and `Permanent/AgentService`; Linux `Program`, `Gui/SessionHosts` and `Permanent/AgentService`). For installed
   agents decide whether it runs in the service or in the user's session (`UserSessionBridge` forwarded channels).
4. **Viewer** – handle the payload case in `src/Viewer/web/src/session.ts` and send with `session.send(...)`; UI in
   `ViewerPage` (server-rendered markup) and a module under `src/Viewer/web/src`.
5. **Tests** – handler tests with the fakes in `tests/MKITS.SupportSuite.Agent.Core.Tests/Fakes.cs`, viewer tests with
   vitest, and an end-to-end relay test if the routing or permissions change.

## A platform capability

1. Interface in `Agent.Core/Features` (or extend one with a default member, as `IScreenSource.CaptureStream`, so
   other platforms keep compiling).
2. Implementations in `Agent.Windows/Platform` and `Agent.Linux/Platform` (native calls only through `Native/`, with
   `LibraryImport` and blittable structs); a fallback when the OS lacks the feature (e.g. DXGI → GDI, H.264 → JPEG).
3. A fake for the core tests; platform tests that skip gracefully where CI cannot run them.

## A permission

1. `ViewerPermissions` and its wire name in `ChannelPermissions` (`src/Common/.../Protocol/ChannelPermissions.cs`) –
   include it in `All`.
2. `DevicePermissions` in `Features/Devices/DeviceEntities.cs` (same value), the checkbox list in
   `Components/Admin/Pages/Devices.razor`, `Permission_<Name>` in `SharedResource`, implications in
   `DeviceService.SetPermissionAsync`.
3. `Permission_<wire name>` in **both** agents' `Strings` (consent prompts list them; `PermissionTextTests` fails
   otherwise).
4. Enforcement: a channel permission in `ChannelPermissions.Required`, or – for checks inside a channel – in the agent
   via `ISessionContext.PermissionsOf` (example: `shell_admin`, ADR-0029).
5. Document it in the data model and the administration guides.

## A dependency

Versions only in `Directory.Packages.props` (NuGet) or the `package.json` of the npm project; licenses must be
AGPL-compatible; add the package to `THIRD-PARTY-NOTICES.md`. Dependabot updates them. Prefer the framework or a few
lines of own code over a new package for small things (examples: the Prometheus exporter, the TOTP helper in the
end-to-end tests).

## A decision

Architecture, security, protocol, privileges or platform choices get an ADR: copy [adr/template.md](adr/template.md)
to the next number, add it to [adr/README.md](adr/README.md), and link it from the code where it matters. Never
contradict an accepted ADR – write a new one that supersedes it (as ADR-0029 did for the permission point of
ADR-0028).
