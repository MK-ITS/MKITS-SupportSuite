// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Endpoints;
using MKITS.SupportSuite.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddMkitsInfrastructure();

var app = builder.Build();

app.UseMkitsInfrastructure();

app.MapHealthChecks("/health");
app.MapSystemEndpoints();

await app.RunAsync().ConfigureAwait(false);

/// <summary>
/// Entry point type. Declared partial so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.
/// </summary>
public partial class Program;
