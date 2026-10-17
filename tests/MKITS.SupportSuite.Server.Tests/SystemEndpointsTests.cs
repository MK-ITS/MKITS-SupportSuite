// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Server.Endpoints;

namespace MKITS.SupportSuite.Server.Tests;

public sealed class SystemEndpointsTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_ReturnsOk()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Version_ReturnsProductAndLicense()
    {
        using var client = factory.CreateClient();

        var version = await client.GetFromJsonAsync<SystemEndpoints.VersionResponse>(
            new Uri("/api/system/version", UriKind.Relative));

        Assert.NotNull(version);
        Assert.Equal(ProductInfo.Name, version.Product);
        Assert.Equal("AGPL-3.0-only", version.License);
        Assert.Equal(ProductInfo.SourceCodeUrl, version.SourceCodeUrl);
    }
}
