// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>
/// fail2ban-style brute-force protection, bound from <c>Mkits:IpBan</c> (docs/security-concept.md §5).
/// </summary>
public sealed class IpBanOptions
{
    public const string SectionName = "Mkits:IpBan";

    /// <summary>Failures within <see cref="Window"/> that trigger a ban. Default 5.</summary>
    public int MaxFailures { get; set; } = 5;

    /// <summary>Sliding counting window. Default 10 minutes.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Length of automatic bans. Default 7 days.</summary>
    public TimeSpan BanDuration { get; set; } = TimeSpan.FromDays(7);

    /// <summary>IPv6 clients are counted and banned per network of this prefix length. Default /64.</summary>
    public int IPv6PrefixLength { get; set; } = 64;

    /// <summary>Networks (CIDR) that are never banned. In addition to entries in the admin UI.</summary>
    public IList<string> AllowList { get; } = [];

    /// <summary>Networks (CIDR) that are always rejected. In addition to entries in the admin UI.</summary>
    public IList<string> DenyList { get; } = [];
}

/// <summary>Validates <see cref="IpBanOptions"/> at startup.</summary>
public sealed class IpBanOptionsValidator : IValidateOptions<IpBanOptions>
{
    public ValidateOptionsResult Validate(string? name, IpBanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MaxFailures is < 1 or > 1000)
        {
            failures.Add($"{IpBanOptions.SectionName}:MaxFailures must be between 1 and 1000.");
        }

        if (options.Window <= TimeSpan.Zero)
        {
            failures.Add($"{IpBanOptions.SectionName}:Window must be positive.");
        }

        if (options.BanDuration <= TimeSpan.Zero)
        {
            failures.Add($"{IpBanOptions.SectionName}:BanDuration must be positive.");
        }

        if (options.IPv6PrefixLength is < 32 or > 128)
        {
            failures.Add($"{IpBanOptions.SectionName}:IPv6PrefixLength must be between 32 and 128.");
        }

        foreach (var (listName, list) in new[] { ("AllowList", options.AllowList), ("DenyList", options.DenyList) })
        {
            foreach (var network in list)
            {
                if (!System.Net.IPNetwork.TryParse(network, out _) && !IPAddress.TryParse(network, out _))
                {
                    failures.Add($"{IpBanOptions.SectionName}:{listName} contains an invalid network: '{network}'.");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
