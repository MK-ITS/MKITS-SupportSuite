// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Server.Infrastructure.Configuration;
using MKITS.SupportSuite.Server.Infrastructure.ReverseProxy;

namespace MKITS.SupportSuite.Server.Tests.Infrastructure;

public sealed class OptionsValidationTests
{
    [Theory]
    [InlineData("https://support.example.com")]
    [InlineData("https://support.example.com:8443")]
    [InlineData("http://localhost:5080")]
    [InlineData("https://example.com/support")]
    public void MkitsOptions_ValidPublicBaseUrl_Succeeds(string url)
    {
        var result = new MkitsOptionsValidator().Validate(null, new MkitsOptions { PublicBaseUrl = url });

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("support.example.com")]
    [InlineData("ftp://support.example.com")]
    [InlineData("https://support.example.com/?a=b")]
    [InlineData("https://support.example.com/#x")]
    public void MkitsOptions_InvalidPublicBaseUrl_Fails(string url)
    {
        var result = new MkitsOptionsValidator().Validate(null, new MkitsOptions { PublicBaseUrl = url });

        Assert.True(result.Failed);
        Assert.Contains("PublicBaseUrl", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void MkitsOptions_EmptyDataDirectory_Fails()
    {
        var result = new MkitsOptionsValidator().Validate(null, new MkitsOptions { DataDirectory = " " });

        Assert.True(result.Failed);
        Assert.Contains("DataDirectory", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void ReverseProxyOptions_ValidAddresses_Succeeds()
    {
        var options = new ReverseProxyOptions();
        options.KnownProxies.Add("10.0.0.5");
        options.KnownProxies.Add("2001:db8::1");
        options.KnownNetworks.Add("172.30.80.0/24");
        options.KnownNetworks.Add("2001:db8::/64");

        var result = new ReverseProxyOptionsValidator().Validate(null, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("10.0.0.300", null)]
    [InlineData("proxy.example.com", null)]
    [InlineData(null, "10.0.0.0/33")]
    [InlineData(null, "10.0.0.0")]
    [InlineData(null, "not-a-network")]
    public void ReverseProxyOptions_InvalidAddresses_Fail(string? proxy, string? network)
    {
        var options = new ReverseProxyOptions();
        if (proxy is not null)
        {
            options.KnownProxies.Add(proxy);
        }

        if (network is not null)
        {
            options.KnownNetworks.Add(network);
        }

        var result = new ReverseProxyOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
    }

    [Fact]
    public void ReverseProxyOptions_ForwardLimitBelowOne_Fails()
    {
        var result = new ReverseProxyOptionsValidator().Validate(null, new ReverseProxyOptions { ForwardLimit = 0 });

        Assert.True(result.Failed);
    }
}
