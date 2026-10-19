// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using MKITS.SupportSuite.Server.Features.Notifications;

namespace MKITS.SupportSuite.Server.Tests.TestSupport;

/// <summary>Collects e-mails instead of sending them.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    public ConcurrentQueue<EmailMessage> Sent { get; } = new();

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>Collects notifications instead of delivering them.</summary>
public sealed class RecordingNotificationOutbox : IAdminNotificationOutbox
{
    public ConcurrentQueue<AdminNotification> Items { get; } = new();

    public void Enqueue(AdminNotification notification) => Items.Enqueue(notification);
}
