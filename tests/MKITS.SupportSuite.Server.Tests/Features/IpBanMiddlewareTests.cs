// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Server.Features.IpBans;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class IpBanMiddlewareTests(MkitsWebApplicationFactory factory)
    : IClassFixture<MkitsWebApplicationFactory>
{
    [Fact]
    public async Task BannedNetwork_GetsBare403_OthersPass()
    {
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<IpBanService>();
        await service.CreateBanAsync("198.51.100.0/24", "test", TimeSpan.FromHours(1), createdByUserId: null);

        using var banned = await GetHealthAsync(client, "198.51.100.42");
        using var other = await GetHealthAsync(client, "203.0.113.1");

        Assert.Equal(HttpStatusCode.Forbidden, banned.StatusCode);
        Assert.Empty(await banned.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task ForwardedForHeader_FromUntrustedClient_IsIgnored()
    {
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<IpBanService>();
        await service.CreateBanAsync("192.0.2.66", "test", TimeSpan.FromHours(1), createdByUserId: null);

        // A banned client must not escape by sending its own X-Forwarded-For.
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health", UriKind.Relative));
        request.Headers.Add(MkitsWebApplicationFactory.TestRemoteIpHeader, "192.0.2.66");
        request.Headers.Add("X-Forwarded-For", "203.0.113.200");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> GetHealthAsync(HttpClient client, string remoteIp)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/health", UriKind.Relative));
        request.Headers.Add(MkitsWebApplicationFactory.TestRemoteIpHeader, remoteIp);
        return await client.SendAsync(request);
    }
}
