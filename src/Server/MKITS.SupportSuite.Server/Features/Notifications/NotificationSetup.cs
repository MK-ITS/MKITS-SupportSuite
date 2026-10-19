// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Features.Notifications;

/// <summary>Registration of e-mail and admin notifications.</summary>
public static class NotificationSetup
{
    public static IServiceCollection AddMkitsNotifications(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SmtpOptions>, SmtpOptionsValidator>();

        services.AddSingleton<IEmailSender, MailKitEmailSender>();
        services.AddSingleton<AdminNotificationOutbox>();
        services.AddSingleton<IAdminNotificationOutbox>(provider => provider.GetRequiredService<AdminNotificationOutbox>());
        services.AddSingleton<AdminNotificationDispatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<AdminNotificationDispatcher>());

        return services;
    }
}
