// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace MKITS.SupportSuite.Server.Tests;

/// <summary>
/// Test host for integration tests: uses a temporary data directory that is deleted afterwards,
/// so tests never write into the source tree.
/// </summary>
public sealed class MkitsWebApplicationFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), $"mkits-tests-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");
        builder.UseSetting("Mkits:DataDirectory", DataDirectory);
        builder.UseSetting("Mkits:PublicBaseUrl", "https://support.example.test");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}
