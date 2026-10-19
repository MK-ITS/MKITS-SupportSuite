// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Infrastructure.ReverseProxy;

/// <summary>
/// Translates <see cref="ReverseProxyOptions"/> into ASP.NET Core's <see cref="ForwardedHeadersOptions"/>.
/// Addresses are validated beforehand by <see cref="ReverseProxyOptionsValidator"/>.
/// </summary>
public sealed class ReverseProxyConfiguration(IOptions<ReverseProxyOptions> proxyOptions)
    : IConfigureOptions<ForwardedHeadersOptions>
{
    public void Configure(ForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var settings = proxyOptions.Value;

        // Host is intentionally not forwarded: nginx passes the original Host header itself.
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = settings.ForwardLimit;

        // Framework defaults already trust loopback; configured proxies are added on top.
        foreach (var proxy in settings.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (var network in settings.KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }
}
