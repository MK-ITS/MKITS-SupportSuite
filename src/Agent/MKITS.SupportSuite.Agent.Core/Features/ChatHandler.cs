// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>Shows chat messages to the end user (chat window / notification).</summary>
public interface IChatPresenter
{
    void ShowMessage(string author, string text, DateTimeOffset timestamp);
}

/// <summary>Chat between the end user and the supporters.</summary>
public sealed class ChatHandler(IChatPresenter presenter, TimeProvider? timeProvider = null) : ISessionChannelHandler
{
    internal const int MaxTextLength = 4000;
    internal const int MaxAuthorLength = 100;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.BodyCase == SessionPayload.BodyOneofCase.ChatMessage && !string.IsNullOrWhiteSpace(payload.ChatMessage.Text))
        {
            var message = payload.ChatMessage;

            // The timestamp is the arrival time: a peer's clock is not trusted for display.
            presenter.ShowMessage(Truncate(message.Author, MaxAuthorLength), Truncate(message.Text, MaxTextLength), _timeProvider.GetUtcNow());
        }

        return Task.CompletedTask;
    }

    /// <summary>Sends a message typed by the end user to all supporters.</summary>
    public Task SendAsync(ISessionContext context, string author, string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.CompletedTask;
        }

        return context.SendAsync(null, new SessionPayload
        {
            ChatMessage = new ChatMessage
            {
                Author = Truncate(author, MaxAuthorLength),
                Text = Truncate(text, MaxTextLength),
                TimestampMs = (ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
            },
        }, cancellationToken);
    }

    private static string Truncate(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];
}
