// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Infrastructure.ReverseProxy;

/// <summary>
/// Which reverse proxies are trusted to supply <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>.
/// Bound from <c>Mkits:ReverseProxy</c>. Loopback (127.0.0.1, ::1) is always trusted.
/// </summary>
/// <remarks>
/// Security-critical: the client IP derived from these headers drives the IP ban service.
/// Only list addresses that really are your proxies – otherwise clients can spoof their IP.
/// </remarks>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "Mkits:ReverseProxy";

    /// <summary>Single proxy addresses, e.g. <c>10.0.0.5</c>.</summary>
    public IList<string> KnownProxies { get; } = [];

    /// <summary>Proxy networks in CIDR notation, e.g. <c>172.30.80.0/24</c> (Docker network of the compose file).</summary>
    public IList<string> KnownNetworks { get; } = [];

    /// <summary>Number of proxy hops to evaluate in <c>X-Forwarded-For</c>. Default 1 (nginx directly in front).</summary>
    public int ForwardLimit { get; set; } = 1;
}
