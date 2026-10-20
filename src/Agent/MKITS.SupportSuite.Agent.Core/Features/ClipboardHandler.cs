// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>Platform clipboard (text only; files go through file transfer).</summary>
public interface IClipboard
{
    string? GetText();

    void SetText(string text);
}

/// <summary>
/// Synchronizes clipboard text both ways: text from a viewer is placed on the local clipboard; local changes
/// (polled with <see cref="PollAsync"/>) are sent to all ready viewers. Text that just arrived is not echoed back.
/// </summary>
public sealed class ClipboardHandler(IClipboard clipboard) : ISessionChannelHandler
{
    /// <summary>Protocol limit (docs/protocol.md §5).</summary>
    internal const int MaxTextLength = 1024 * 1024;

    private readonly object _lock = new();
    private string? _lastKnown;

    public Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.BodyCase != SessionPayload.BodyOneofCase.ClipboardText || payload.ClipboardText.Text.Length > MaxTextLength)
        {
            return Task.CompletedTask;
        }

        // Never call the platform clipboard while holding the lock: implementations may marshal to a UI thread
        // that is itself waiting for the lock (PollAsync runs on a UI timer on Windows).
        lock (_lock)
        {
            _lastKnown = payload.ClipboardText.Text;
        }

        clipboard.SetText(payload.ClipboardText.Text);
        return Task.CompletedTask;
    }

    public Task OnViewerReadyAsync(ISessionContext context, string viewerId, CancellationToken cancellationToken)
    {
        // Do not push the current clipboard to a new supporter unasked – only later changes are shared.
        var current = clipboard.GetText();
        lock (_lock)
        {
            _lastKnown ??= current;
        }

        return Task.CompletedTask;
    }

    /// <summary>Checks the local clipboard and sends a changed text to all viewers. Call about once per second.</summary>
    public async Task PollAsync(ISessionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var text = clipboard.GetText();
        lock (_lock)
        {
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || string.Equals(text, _lastKnown, StringComparison.Ordinal))
            {
                return;
            }

            _lastKnown = text;
        }

        await context.SendAsync(null, new SessionPayload { ClipboardText = new ClipboardText { Text = text } }, cancellationToken).ConfigureAwait(false);
    }
}
