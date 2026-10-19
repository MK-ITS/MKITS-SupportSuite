// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Infrastructure.ReverseProxy;

/// <summary>
/// Validates <see cref="ReverseProxyOptions"/> at startup: every proxy must be a valid IP address,
/// every network valid CIDR notation.
/// </summary>
public sealed class ReverseProxyOptionsValidator : IValidateOptions<ReverseProxyOptions>
{
    public ValidateOptionsResult Validate(string? name, ReverseProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        foreach (var proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                failures.Add($"{ReverseProxyOptions.SectionName}:KnownProxies contains an invalid IP address: '{proxy}'.");
            }
        }

        foreach (var network in options.KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                failures.Add($"{ReverseProxyOptions.SectionName}:KnownNetworks contains an invalid CIDR network: '{network}'.");
            }
        }

        if (options.ForwardLimit < 1)
        {
            failures.Add($"{ReverseProxyOptions.SectionName}:ForwardLimit must be at least 1.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
