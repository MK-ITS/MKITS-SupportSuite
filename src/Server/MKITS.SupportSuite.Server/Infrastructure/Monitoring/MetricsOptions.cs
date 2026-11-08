// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Server.Features.IpBans;

namespace MKITS.SupportSuite.Server.Infrastructure.Monitoring;

/// <summary>
/// The Prometheus endpoint <c>/metrics</c> (14.3), bound from <c>Mkits:Metrics</c>. Off by default; when on, only
/// scrapers from <see cref="AllowedNetworks"/> (default: loopback) or with the bearer <see cref="Token"/> get an answer.
/// </summary>
public sealed class MetricsOptions
{
    public const string SectionName = "Mkits:Metrics";

    public bool Enabled { get; set; }

    /// <summary>Networks (CIDR or single addresses) that may scrape without a token, e.g. the Docker network of Prometheus.</summary>
    public IList<string> AllowedNetworks { get; } = [];

    /// <summary>Optional bearer token (at least 32 characters) for scrapers outside <see cref="AllowedNetworks"/>.</summary>
    public string? Token { get; set; }
}

public sealed class MetricsOptionsValidator : IValidateOptions<MetricsOptions>
{
    public ValidateOptionsResult Validate(string? name, MetricsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Token is { } token && token.Length < 32)
        {
            return ValidateOptionsResult.Fail($"{MetricsOptions.SectionName}:Token must be at least 32 characters long.");
        }

        var invalid = options.AllowedNetworks.Where(n => !IpNetworks.TryParseNetwork(n, out _)).ToList();
        return invalid.Count > 0
            ? ValidateOptionsResult.Fail($"{MetricsOptions.SectionName}:AllowedNetworks contains invalid entries: {string.Join(", ", invalid)}")
            : ValidateOptionsResult.Success;
    }
}
