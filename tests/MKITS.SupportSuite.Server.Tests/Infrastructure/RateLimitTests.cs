// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;

namespace MKITS.SupportSuite.Server.Tests.Infrastructure;

public sealed class RateLimitTests
{
    [Fact]
    public async Task AuthSubmissions_AreLimitedPerAddress()
    {
        using var factory = new MkitsWebApplicationFactory { Settings = new Dictionary<string, string> { ["Mkits:RateLimits:AuthPerMinute"] = "2" } };
        using var client = factory.CreateClient();

        var first = await PostAsync(client, "/portal/code", "203.0.113.7");
        var second = await PostAsync(client, "/account/login", "203.0.113.7");
        using var third = await SendAsync(client, HttpMethod.Post, "/setup", "203.0.113.7");
        var otherAddress = await PostAsync(client, "/portal/code", "203.0.113.8");
        var pageView = await SendStatusAsync(client, HttpMethod.Get, "/portal/code", "203.0.113.7");

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, second);
        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
        Assert.Equal("60", third.Headers.GetValues("Retry-After").Single());
        Assert.NotEqual(HttpStatusCode.TooManyRequests, otherAddress);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, pageView); // only submissions count
    }

    [Fact]
    public async Task WebSocketAttempts_AreLimited_LoopbackAndUnknownAreNot()
    {
        using var factory = new MkitsWebApplicationFactory { Settings = new Dictionary<string, string> { ["Mkits:RateLimits:ConnectionsPerMinute"] = "1" } };
        using var client = factory.CreateClient();

        var first = await SendStatusAsync(client, HttpMethod.Get, "/ws/agent", "198.51.100.20");
        var second = await SendStatusAsync(client, HttpMethod.Get, "/ws/viewer", "198.51.100.20");
        var loopback = new[]
        {
            await SendStatusAsync(client, HttpMethod.Get, "/ws/agent", "127.0.0.1"),
            await SendStatusAsync(client, HttpMethod.Get, "/ws/agent", "127.0.0.1"),
        };
        var unknown = new[] { await SendStatusAsync(client, HttpMethod.Get, "/ws/agent", null), await SendStatusAsync(client, HttpMethod.Get, "/ws/agent", null) };

        Assert.NotEqual(HttpStatusCode.TooManyRequests, first);
        Assert.Equal(HttpStatusCode.TooManyRequests, second);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, loopback);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, unknown);
    }

    [Fact]
    public async Task AllowListedAddresses_AreNotLimited()
    {
        using var factory = new MkitsWebApplicationFactory
        {
            Settings = new Dictionary<string, string>
            {
                ["Mkits:RateLimits:AuthPerMinute"] = "1",
                ["Mkits:IpBan:AllowList:0"] = "192.0.2.0/24",
            },
        };
        using var client = factory.CreateClient();

        var statuses = new[]
        {
            await PostAsync(client, "/portal/code", "192.0.2.10"),
            await PostAsync(client, "/portal/code", "192.0.2.10"),
            await PostAsync(client, "/portal/code", "192.0.2.10"),
        };

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task Disabled_LimitsNothing()
    {
        using var factory = new MkitsWebApplicationFactory
        {
            Settings = new Dictionary<string, string> { ["Mkits:RateLimits:Enabled"] = "false", ["Mkits:RateLimits:AuthPerMinute"] = "1" },
        };
        using var client = factory.CreateClient();

        var statuses = new[] { await PostAsync(client, "/setup", "203.0.113.9"), await PostAsync(client, "/setup", "203.0.113.9") };

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses);
    }

    private static Task<HttpStatusCode> PostAsync(HttpClient client, string path, string address) =>
        SendStatusAsync(client, HttpMethod.Post, path, address);

    private static async Task<HttpStatusCode> SendStatusAsync(HttpClient client, HttpMethod method, string path, string? address)
    {
        using var response = await SendAsync(client, method, path, address);
        return response.StatusCode;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? address)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (address is not null)
        {
            request.Headers.Add(MkitsWebApplicationFactory.TestRemoteIpHeader, address);
        }

        if (method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent([]);
        }

        return await client.SendAsync(request);
    }
}
