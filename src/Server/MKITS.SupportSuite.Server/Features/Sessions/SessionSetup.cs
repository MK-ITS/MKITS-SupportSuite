// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Features.Sessions;

/// <summary>Registration of the session feature.</summary>
public static class SessionSetup
{
    public static IServiceCollection AddMkitsSessions(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SessionOptions>()
            .Bind(configuration.GetSection(SessionOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SessionOptions>, SessionOptionsValidator>();

        services.AddSingleton<ViewerTicketStore>();
        services.AddSingleton<SessionService>();
        services.AddHostedService<SessionMaintenance>();

        return services;
    }

    /// <summary>Startup step: ends sessions whose connections were lost by a restart.</summary>
    public static async Task InitializeSessionsAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        await app.Services.GetRequiredService<SessionService>().EndInterruptedSessionsAsync().ConfigureAwait(false);
    }
}

/// <summary>Expires codes and over-long sessions, and drops unused viewer tickets.</summary>
public sealed partial class SessionMaintenance(SessionService sessions, ViewerTicketStore tickets, TimeProvider timeProvider, ILogger<SessionMaintenance> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await sessions.ExpireAsync(stoppingToken).ConfigureAwait(false);
                tickets.Purge(timeProvider.GetUtcNow());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogMaintenanceFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Session maintenance failed")]
    private static partial void LogMaintenanceFailed(ILogger logger, Exception exception);
}
