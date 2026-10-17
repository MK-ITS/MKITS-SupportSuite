// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Server.Infrastructure.ReverseProxy;

namespace MKITS.SupportSuite.Server.Tests.Infrastructure;

public sealed class ReverseProxyConfigurationTests
{
    [Fact]
    public void Configure_AddsConfiguredProxiesAndNetworks_KeepsLoopback()
    {
        var settings = new ReverseProxyOptions { ForwardLimit = 2 };
        settings.KnownProxies.Add("10.0.0.5");
        settings.KnownNetworks.Add("172.30.80.0/24");
        var forwarded = new ForwardedHeadersOptions();

        new ReverseProxyConfiguration(Options.Create(settings)).Configure(forwarded);

        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, forwarded.ForwardedHeaders);
        Assert.Equal(2, forwarded.ForwardLimit);
        Assert.Contains(IPAddress.Parse("10.0.0.5"), forwarded.KnownProxies);
        Assert.Contains(IPAddress.Loopback, forwarded.KnownProxies);
        Assert.Contains(forwarded.KnownIPNetworks, n => n.Contains(IPAddress.Parse("172.30.80.1")));
    }

    [Fact]
    public void Configure_DoesNotForwardHostHeader()
    {
        var forwarded = new ForwardedHeadersOptions();

        new ReverseProxyConfiguration(Options.Create(new ReverseProxyOptions())).Configure(forwarded);

        Assert.False(forwarded.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost));
    }
}
