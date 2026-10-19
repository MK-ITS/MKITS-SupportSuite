// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Infrastructure.Security;

namespace MKITS.SupportSuite.Server.Tests.Infrastructure;

public sealed class SecurityHeadersTests(MkitsWebApplicationFactory factory)
    : IClassFixture<MkitsWebApplicationFactory>
{
    [Theory]
    [InlineData("/health")]
    [InlineData("/api/system/version")]
    [InlineData("/does-not-exist")]
    public async Task AnyResponse_ContainsSecurityHeaders(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy, response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Response_DoesNotAdvertiseServer()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.False(response.Headers.Contains("Server"));
    }
}
