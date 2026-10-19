# ADR-0020: Blazor Web App for the server UI

- **Status:** Accepted
- **Date:** 2026-10-18

## Context

The server needs a web UI for the customer portal, supporters and administrators (WORKPLAN Phase 6, 4B). The UI must be localizable (EN/DE), work under a strict Content-Security-Policy (no inline scripts), integrate with ASP.NET Core Identity (cookies, TOTP) and be maintainable in C#. The project owner asked for Blazor and for Visual Studio 2026 compatibility.

## Options considered

1. **Razor Pages** – simple, server-rendered; less interactive.
2. **Blazor Web App** – components in C#, static server rendering where needed, interactive server rendering where useful, first-class Identity integration in .NET 10.
3. **SPA (TypeScript framework) + API** – separate tool chain, more code to keep in sync.

## Decision

**Blazor Web App (.NET 10)** in the server project (`src/Server/.../Components`):

- **Per-page interactivity:** `<Routes />` in `App.razor` has no global render mode, so every page is rendered statically on the server (SSR) unless it declares `@rendermode InteractiveServer`.
- **Static SSR** for everything that touches authentication cookies or must work without a live connection: login, two-factor login, setup, logout, profile, customer portal. These pages simply declare no render mode – they must never get one, because they need `HttpContext`.
- **Interactive Server** render mode for supporter and admin pages (live lists, dialogs). No WebAssembly for now.
- No inline scripts or styles: only `blazor.web.js` and static CSS from the same origin, so the existing CSP stays strict. The reconnect UI is a custom element styled in `app.css`.
- nginx must forward WebSocket upgrades for `/_blazor` (interactive circuits) in addition to `/ws/`.
- UI texts come from `Resources/SharedResource*.resx` via `IStringLocalizer<SharedResource>`.

## Consequences

- Interactive pages hold a SignalR circuit per browser tab (memory per connected Admin/User – fine for the expected scale).
- Authentication state of circuits is revalidated periodically (security stamp), so disabling a user also ends their open admin tabs.
- The browser viewer (Phase 9) stays a separate TypeScript module; it can be hosted inside a Blazor page.
- Works with Visual Studio 2026 (Blazor tooling, Hot Reload) and `dotnet watch`.
