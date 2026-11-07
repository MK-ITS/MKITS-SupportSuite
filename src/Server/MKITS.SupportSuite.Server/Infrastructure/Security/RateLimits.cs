// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Server.Features.IpBans;

namespace MKITS.SupportSuite.Server.Infrastructure.Security;

/// <summary>
/// In-app rate limits per client network (4.4: per IPv4 address, per IPv6 /64 by default), for deployments without the nginx limits of <c>deploy/nginx</c> and as
/// a second line behind them. Bound from <c>Mkits:RateLimits</c>. Failed attempts are additionally counted towards IP
/// bans (<see cref="IpBanService"/>); these limits also stop floods of attempts that are not (yet) failures.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Mkits:RateLimits";

    public bool Enabled { get; set; } = true;

    /// <summary>Login, setup, two-factor and session-code submissions per address and minute.</summary>
    public int AuthPerMinute { get; set; } = 20;

    /// <summary>
    /// Agent and viewer WebSocket connection attempts per address and minute. Generous: many installed agents behind one
    /// NAT address reconnect together after a server restart (they back off when refused).
    /// </summary>
    public int ConnectionsPerMinute { get; set; } = 300;
}

/// <summary>Validates <see cref="RateLimitOptions"/> at startup.</summary>
public sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AuthPerMinute is < 1 or > 10_000)
        {
            return ValidateOptionsResult.Fail($"{RateLimitOptions.SectionName}:AuthPerMinute must be between 1 and 10000.");
        }

        return options.ConnectionsPerMinute is < 1 or > 100_000
            ? ValidateOptionsResult.Fail($"{RateLimitOptions.SectionName}:ConnectionsPerMinute must be between 1 and 100000.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>Which limit a request counts against.</summary>
internal enum RateLimitBucket
{
    None,
    Auth,
    Connections,
}

internal static class RateLimits
{
    private static readonly string[] AuthPaths = ["/account/login", "/account/login-2fa", "/account/login-recovery", "/setup", "/portal/code", "/portal/download"];

    public static IServiceCollection AddMkitsRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.OnRejected = (context, _) =>
            {
                context.HttpContext.Response.Headers.RetryAfter = "60";
                return ValueTask.CompletedTask;
            };
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var options = context.RequestServices.GetRequiredService<IOptionsMonitor<RateLimitOptions>>().CurrentValue;
                var bucket = Classify(context.Request);
                var address = context.Connection.RemoteIpAddress;
                if (!options.Enabled || bucket == RateLimitBucket.None || IsExempt(address, context.RequestServices.GetRequiredService<IpBanService>()))
                {
                    return RateLimitPartition.GetNoLimiter("none");
                }

                // Counted per client network like failures for IP bans: one IPv6 /64 (by default) is one client.
                var limit = bucket == RateLimitBucket.Auth ? options.AuthPerMinute : options.ConnectionsPerMinute;
                var prefix = context.RequestServices.GetRequiredService<IOptionsMonitor<IpBanOptions>>().CurrentValue.IPv6PrefixLength;
                var key = string.Create(CultureInfo.InvariantCulture, $"{bucket}:{limit}:{IpNetworks.ClientNetwork(address!, prefix)}");
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                });
            });
        });
        return services;
    }

    /// <summary>The limit a request counts against: WebSocket connections, submissions of auth forms, or none.</summary>
    internal static RateLimitBucket Classify(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (path.StartsWith("/ws/", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitBucket.Connections;
        }

        return HttpMethods.IsPost(request.Method) && AuthPaths.Any(p => string.Equals(path.TrimEnd('/'), p, StringComparison.OrdinalIgnoreCase))
            ? RateLimitBucket.Auth
            : RateLimitBucket.None;
    }

    /// <summary>Unknown and loopback addresses (health checks, local tools) and the IP allow list are not limited.</summary>
    internal static bool IsExempt(IPAddress? address, IpBanService ipBans) =>
        address is null || IPAddress.IsLoopback(address) || ipBans.IsAllowListed(address);
}
