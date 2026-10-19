// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using MKITS.SupportSuite.Server.Features.IpBans;

namespace MKITS.SupportSuite.Server.Tests.Features;

public sealed class IpNetworksTests
{
    [Theory]
    [InlineData("203.0.113.7", 64, "203.0.113.7/32")]
    [InlineData("::ffff:203.0.113.7", 64, "203.0.113.7/32")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", 64, "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", 48, "2001:db8:1::/48")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", 128, "2001:db8:1:2:aaaa:bbbb:cccc:dddd/128")]
    public void ClientNetwork_AggregatesAsConfigured(string address, int prefix, string expected)
    {
        var network = IpNetworks.ClientNetwork(IPAddress.Parse(address), prefix);

        Assert.Equal(expected, network.ToString());
    }

    [Theory]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("198.51.100.4", "198.51.100.4/32")]
    [InlineData("2001:db8::1", "2001:db8::1/128")]
    public void TryParseNetwork_AcceptsCidrAndPlainAddresses(string value, string expected)
    {
        Assert.True(IpNetworks.TryParseNetwork(value, out var network));
        Assert.Equal(expected, network.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    public void TryParseNetwork_RejectsInvalid(string value)
    {
        Assert.False(IpNetworks.TryParseNetwork(value, out _));
    }
}
