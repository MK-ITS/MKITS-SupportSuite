// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Infrastructure.Monitoring;
using MKITS.SupportSuite.Server.Tests.TestSupport;

namespace MKITS.SupportSuite.Server.Tests.Infrastructure;

public sealed class MetricsTests
{
    private const string Token = "a-scrape-token-of-at-least-32-characters";

    [Fact]
    public async Task Disabled_ByDefault_NotFound()
    {
        using var factory = new MkitsWebApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/metrics", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", null, HttpStatusCode.OK)] // loopback
    [InlineData("10.20.0.7", null, HttpStatusCode.OK)] // configured network
    [InlineData("203.0.113.5", null, HttpStatusCode.Forbidden)]
    [InlineData("203.0.113.5", "wrong-token-wrong-token-wrong-token", HttpStatusCode.Forbidden)]
    [InlineData("203.0.113.5", Token, HttpStatusCode.OK)]
    [InlineData("203.0.113.5", "a-scrape-token-of-at-least-32-character", HttpStatusCode.Forbidden)] // one character short
    public async Task Access_LoopbackNetworksOrToken(string address, string? token, HttpStatusCode expected)
    {
        using var factory = Enabled();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/metrics", UriKind.Relative));
        request.Headers.Add(MkitsWebApplicationFactory.TestRemoteIpHeader, address);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Scrape_ShowsRelayTrafficSessionsAndRuntime()
    {
        using var factory = Enabled();
        _ = factory.Server;
        Guid supporterId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
            var supporter = new MkitsUser { Id = Guid.NewGuid(), UserName = "metrics", Email = "metrics@example.test", DisplayName = "Metrics" };
            Assert.True((await users.CreateAsync(supporter, "a long test passphrase")).Succeeded);
            Assert.True((await users.AddToRoleAsync(supporter, Roles.User)).Succeeded);
            supporterId = supporter.Id;
        }

        var created = Assert.IsType<CreatedSession>((await factory.Services.GetRequiredService<SessionService>().CreateAdHocAsync(supporterId, "metrics")).Session);
        var (agent, _, _) = await TestPeer.ConnectAgentAsync(factory.Server, created.Code);
        await using (agent)
        {
            var text = await ScrapeAsync(factory);

            Assert.Contains("# TYPE mkits_sessions_live gauge", text, StringComparison.Ordinal);
            Assert.Matches(@"mkits_sessions_live [1-9]", text);
            Assert.Matches(@"mkits_relay_connections\{role=""agent""\} [1-9]", text);
            Assert.Matches(@"mkits_relay_bytes_total\{direction=""in"",role=""agent""\} [1-9]", text);
            Assert.Matches(@"mkits_relay_bytes_total\{direction=""out"",role=""agent""\} [1-9]", text);
            Assert.Contains("dotnet_", text, StringComparison.Ordinal); // .NET runtime meter
            Assert.DoesNotContain(created.Code, text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("bearer " + Token, true)] // the scheme is case-insensitive
    [InlineData("Basic " + Token, false)]
    [InlineData(Token, false)]
    [InlineData("", false)]
    public void IsAllowed_ChecksSchemeAndToken(string authorization, bool expected)
    {
        var options = new MetricsOptions { Token = Token };

        Assert.Equal(expected, MetricsSetup.IsAllowed(IPAddress.Parse("203.0.113.5"), authorization, options));
    }

    [Fact]
    public void Validation_RejectsShortTokensAndBadNetworks()
    {
        var validator = new MetricsOptionsValidator();
        var options = new MetricsOptions { Token = "short" };
        Assert.True(validator.Validate(null, options).Failed);

        options = new MetricsOptions();
        options.AllowedNetworks.Add("not-a-network");
        Assert.True(validator.Validate(null, options).Failed);

        options = new MetricsOptions { Token = Token };
        options.AllowedNetworks.Add("172.18.0.0/16");
        Assert.True(validator.Validate(null, options).Succeeded);
    }

    private static MkitsWebApplicationFactory Enabled() => new()
    {
        Settings = new Dictionary<string, string>
        {
            ["Mkits:Metrics:Enabled"] = "true",
            ["Mkits:Metrics:Token"] = Token,
            ["Mkits:Metrics:AllowedNetworks:0"] = "10.20.0.0/16",
        },
    };

    private static async Task<string> ScrapeAsync(MkitsWebApplicationFactory factory)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/metrics", UriKind.Relative));
        request.Headers.Add(MkitsWebApplicationFactory.TestRemoteIpHeader, "127.0.0.1");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.MediaType, StringComparison.Ordinal);
        return await response.Content.ReadAsStringAsync();
    }
}
