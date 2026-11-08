// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Server.Features.IpBans;

namespace MKITS.SupportSuite.Server.Infrastructure.Monitoring;

public static class MetricsSetup
{
    public static IServiceCollection AddMkitsMetrics(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMetrics();
        services.AddOptions<MetricsOptions>().Bind(configuration.GetSection(MetricsOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetricsOptions>, MetricsOptionsValidator>();
        services.AddSingleton<MkitsMetrics>();
        services.AddSingleton<PrometheusExporter>();
        return services;
    }

    /// <summary><c>GET /metrics</c> – 404 while disabled, 403 for scrapers that are neither allowed nor have the token.</summary>
    public static IEndpointRouteBuilder MapMetricsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/metrics", (HttpContext context, IOptionsMonitor<MetricsOptions> options, PrometheusExporter exporter) =>
        {
            var settings = options.CurrentValue;
            if (!settings.Enabled)
            {
                return Results.NotFound();
            }

            if (!IsAllowed(context.Connection.RemoteIpAddress, context.Request.Headers.Authorization.ToString(), settings))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(exporter.Render(), "text/plain; version=0.0.4; charset=utf-8");
        }).ExcludeFromDescription();
        return endpoints;
    }

    /// <summary>Loopback and the configured networks without a token; anyone else only with the bearer token.</summary>
    internal static bool IsAllowed(IPAddress? address, string authorization, MetricsOptions options)
    {
        if (address is not null)
        {
            var ip = IpNetworks.Normalize(address);
            if (IPAddress.IsLoopback(ip)
                || options.AllowedNetworks.Any(n => IpNetworks.TryParseNetwork(n, out var network) && network.Contains(ip)))
            {
                return true;
            }
        }

        if (options.Token is not { Length: > 0 } token || !authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization["Bearer ".Length..]), Encoding.UTF8.GetBytes(token));
    }
}
