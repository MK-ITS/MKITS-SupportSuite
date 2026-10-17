// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHealthChecks();

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapSystemEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Entry point type. Declared partial so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
