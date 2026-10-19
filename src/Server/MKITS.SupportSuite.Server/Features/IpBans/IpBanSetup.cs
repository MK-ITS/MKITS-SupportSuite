// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>Registration of the IP ban feature.</summary>
public static class IpBanSetup
{
    public static IServiceCollection AddMkitsIpBans(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IpBanOptions>()
            .Bind(configuration.GetSection(IpBanOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<IpBanOptions>, IpBanOptionsValidator>();

        services.AddSingleton<FailureTracker>();
        services.AddSingleton<IpBanService>();
        services.AddHostedService<IpBanMaintenance>();

        return services;
    }
}

/// <summary>
/// Periodically drops stale failure counters and reloads bans so that expired bans disappear from the snapshot.
/// </summary>
public sealed class IpBanMaintenance(
    IpBanService ipBanService,
    FailureTracker failureTracker,
    IOptionsMonitor<IpBanOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            failureTracker.Purge(timeProvider.GetUtcNow(), options.CurrentValue.Window);
            await ipBanService.ReloadAsync(stoppingToken).ConfigureAwait(false);
        }
    }
}
