// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Endpoints;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Infrastructure;
using MKITS.SupportSuite.Server.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// ---- services ----
builder.AddMkitsInfrastructure();
builder.Services
    .AddMkitsDatabase()
    .AddMkitsAudit()
    .AddMkitsIpBans(builder.Configuration)
    .AddMkitsIdentity();

var app = builder.Build();

// ---- startup tasks ----
app.PrepareDataDirectory();
await app.InitializeDatabaseAsync().ConfigureAwait(false);
await app.Services.GetRequiredService<IpBanService>().ReloadAsync().ConfigureAwait(false);
await app.Services.GetRequiredService<IAuditLog>()
    .WriteAsync(new AuditEntry(AuditEventTypes.ServerStarted, AuditActorType.System))
    .ConfigureAwait(false);

// ---- middleware (order matters) ----
app.UseMkitsForwardedHeaders();       // 1. real client IP from trusted proxies
app.UseMiddleware<IpBanMiddleware>(); // 2. reject banned networks before anything else runs
app.UseMkitsWebBasics();              // 3. security headers, error handling, localization

// ---- endpoints ----
app.MapHealthChecks("/health");
app.MapSystemEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Entry point type. Declared partial so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
