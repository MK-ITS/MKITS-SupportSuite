// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Server.Features.Identity;

namespace MKITS.SupportSuite.Server.Features.Notifications;

/// <summary>
/// A notification for all active Admins. Subject and body are resource keys, formatted with
/// <see cref="Arguments"/> in each recipient's UI language.
/// </summary>
public sealed record AdminNotification(string SubjectKey, string BodyKey, IReadOnlyList<string> Arguments)
{
    public static AdminNotification IpBanned(string network, string reason, DateTimeOffset? expiresAt) => new(
        "Mail_IpBanned_Subject",
        "Mail_IpBanned_Body",
        [network, reason, expiresAt?.ToString("u", CultureInfo.InvariantCulture) ?? "∞"]);
}

/// <summary>Non-blocking hand-over of notifications; delivery happens in the background.</summary>
public interface IAdminNotificationOutbox
{
    void Enqueue(AdminNotification notification);
}

/// <summary>Bounded in-memory queue. When full, the oldest notifications are dropped (never blocks callers).</summary>
public sealed class AdminNotificationOutbox : IAdminNotificationOutbox
{
    private readonly Channel<AdminNotification> _channel = Channel.CreateBounded<AdminNotification>(
        new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<AdminNotification> Reader => _channel.Reader;

    public void Enqueue(AdminNotification notification) => _channel.Writer.TryWrite(notification);
}

/// <summary>
/// Delivers queued notifications to every active Admin with an e-mail address, localized per recipient,
/// limited to <see cref="SmtpOptions.MaxNotificationsPerHour"/> e-mails per hour.
/// Does nothing (but drains the queue) while e-mail is not configured.
/// </summary>
public sealed partial class AdminNotificationDispatcher(
    AdminNotificationOutbox queue,
    IServiceScopeFactory scopeFactory,
    IEmailSender emailSender,
    IStringLocalizer<SharedResource> localizer,
    IOptionsMonitor<SmtpOptions> options,
    TimeProvider timeProvider,
    ILogger<AdminNotificationDispatcher> logger) : BackgroundService
{
    private readonly Queue<DateTimeOffset> _sent = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var notification in queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!options.CurrentValue.IsConfigured)
            {
                continue;
            }

            try
            {
                await DeliverAsync(notification, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A broken mail server must never affect the server itself.
                LogDeliveryFailed(logger, notification.SubjectKey, ex);
            }
        }
    }

    internal async Task DeliverAsync(AdminNotification notification, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var admins = await userManager.GetUsersInRoleAsync(Roles.Admin).ConfigureAwait(false);

        foreach (var admin in admins.Where(a => !a.IsDisabled && !string.IsNullOrWhiteSpace(a.Email)))
        {
            if (!TryTakeQuota())
            {
                LogThrottled(logger, notification.SubjectKey);
                return;
            }

            await emailSender.SendAsync(Render(notification, admin), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Formats subject and body in the admin's UI language (English if none is set).</summary>
    internal EmailMessage Render(AdminNotification notification, MkitsUser admin)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(CultureCookie.IsSupported(admin.UiCulture) ? admin.UiCulture! : "en");
            var args = notification.Arguments.Cast<object>().ToArray();
            return new EmailMessage(admin.Email!, localizer[notification.SubjectKey, args], localizer[notification.BodyKey, args]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private bool TryTakeQuota()
    {
        var now = timeProvider.GetUtcNow();
        while (_sent.Count > 0 && _sent.Peek() <= now - TimeSpan.FromHours(1))
        {
            _sent.Dequeue();
        }

        if (_sent.Count >= options.CurrentValue.MaxNotificationsPerHour)
        {
            return false;
        }

        _sent.Enqueue(now);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending notification {Notification} failed")]
    private static partial void LogDeliveryFailed(ILogger logger, string notification, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification {Notification} dropped: hourly e-mail limit reached")]
    private static partial void LogThrottled(ILogger logger, string notification);
}
