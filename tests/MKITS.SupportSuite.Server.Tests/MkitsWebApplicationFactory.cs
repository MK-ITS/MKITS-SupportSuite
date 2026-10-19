// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace MKITS.SupportSuite.Server.Tests;

/// <summary>
/// Test host for integration tests: uses a temporary data directory that is deleted afterwards,
/// so tests never write into the source tree.
/// Requests may set the header <see cref="TestRemoteIpHeader"/> to simulate a client address.
/// </summary>
public sealed class MkitsWebApplicationFactory : WebApplicationFactory<Program>
{
    /// <summary>Test-only header: the value becomes <c>HttpContext.Connection.RemoteIpAddress</c>.</summary>
    public const string TestRemoteIpHeader = "X-Test-Remote-Ip";

    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"mkits-tests-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Mkits:DataDirectory", DataDirectory);
        builder.UseSetting("Mkits:PublicBaseUrl", "https://support.example.test");
        builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(DataDirectory))
        {
            // Pooled SQLite connections keep the file open (blocks deletion on Windows).
            SqliteConnection.ClearAllPools();
            Directory.Delete(DataDirectory, recursive: true);
        }
    }

    /// <summary>Runs before every other middleware (startup filters wrap the whole pipeline).</summary>
    private sealed class TestRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(TestRemoteIpHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out var address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
