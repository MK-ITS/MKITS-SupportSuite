// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>
/// Passes payloads and ready viewers to <paramref name="inner"/> handlers only while <paramref name="enabled"/> is true –
/// e.g. the service's own screen and input while the desktop is not handled in the user's session. Departures are
/// always passed on, so nothing stays pressed or registered.
/// </summary>
public sealed class SwitchableHandler(Func<bool> enabled, params ISessionChannelHandler[] inner) : ISessionChannelHandler
{
    public async Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        if (!enabled())
        {
            return;
        }

        foreach (var handler in inner)
        {
            await handler.HandleAsync(context, viewerId, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OnViewerReadyAsync(ISessionContext context, string viewerId, CancellationToken cancellationToken)
    {
        if (!enabled())
        {
            return;
        }

        foreach (var handler in inner)
        {
            await handler.OnViewerReadyAsync(context, viewerId, cancellationToken).ConfigureAwait(false);
        }
    }

    public void OnViewerGone(string viewerId)
    {
        foreach (var handler in inner)
        {
            handler.OnViewerGone(viewerId);
        }
    }
}
