// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Components;
using MKITS.SupportSuite.Server.Endpoints;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.Downloads;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Features.Notifications;
using MKITS.SupportSuite.Server.Features.Relay;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Infrastructure;
using MKITS.SupportSuite.Server.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ---- services ----
builder.AddMkitsInfrastructure();
builder.Services
    .AddMkitsDatabase()
    .AddMkitsAudit()
    .AddMkitsIpBans(builder.Configuration)
    .AddMkitsNotifications(builder.Configuration)
    .AddMkitsSessions(builder.Configuration)
    .AddMkitsRelay()
    .AddMkitsIdentity(builder.Environment);

// Web UI (ADR-0020): static SSR by default, interactive server rendering per page.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// ---- startup tasks ----
app.PrepareDataDirectory();
await app.InitializeDatabaseAsync().ConfigureAwait(false);
await app.InitializeIdentityAsync().ConfigureAwait(false);
await app.InitializeSessionsAsync().ConfigureAwait(false);
await app.Services.GetRequiredService<IpBanService>().ReloadAsync().ConfigureAwait(false);
await app.Services.GetRequiredService<IAuditLog>()
    .WriteAsync(new AuditEntry(AuditEventTypes.ServerStarted, AuditActorType.System))
    .ConfigureAwait(false);

// ---- middleware (order matters) ----
app.UseMkitsForwardedHeaders();       // 1. real client IP from trusted proxies
app.UseMiddleware<IpBanMiddleware>(); // 2. reject banned networks before anything else runs (incl. WebSocket upgrades)
app.UseWebSockets();                  //    WebSocket support for the relay (/ws/agent, /ws/viewer)
app.UseMkitsWebBasics();              // 3. security headers, error handling, localization
app.UseAuthentication();              // 4. who is it
app.UseAuthorization();               // 5. may they
app.UseAntiforgery();                 // 6. CSRF protection for forms

// ---- endpoints ----
app.MapHealthChecks("/health");
app.MapSystemEndpoints();
app.MapAccountEndpoints();
app.MapRelayEndpoints();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Entry point type. Declared partial so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
