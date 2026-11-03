// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Features;

/// <summary>
/// Splits the shell channel between two places (ADR-0028): shells opened with <c>elevated</c> run in
/// <paramref name="elevated"/> (the service, with its rights); everything else – normal shells and all other channels
/// – goes to <paramref name="inner"/> (e.g. the bridge to the user's session window). Later shell payloads follow the
/// shell id they belong to, so data for an elevated shell never reaches the user's session and vice versa.
/// </summary>
public sealed class ElevatedShellRouter(ISessionChannelHandler inner, ShellHandler elevated) : ISessionChannelHandler, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, byte> _elevatedShells = new(StringComparer.Ordinal);

    public Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        switch (payload.BodyCase)
        {
            case SessionPayload.BodyOneofCase.ShellOpen when payload.ShellOpen.Elevated:
                _elevatedShells[Key(viewerId, payload.ShellOpen.ShellId)] = 0;
                return elevated.HandleAsync(context, viewerId, payload, cancellationToken);
            case SessionPayload.BodyOneofCase.ShellOpen when _elevatedShells.ContainsKey(Key(viewerId, payload.ShellOpen.ShellId)):
                // A second shell under an id that is already elevated: the elevated handler refuses the duplicate id, so
                // later data for that id can never be split between two shells.
                return elevated.HandleAsync(context, viewerId, payload, cancellationToken);
            case SessionPayload.BodyOneofCase.ShellData:
                return Route(context, viewerId, payload, payload.ShellData.ShellId, closes: false, cancellationToken);
            case SessionPayload.BodyOneofCase.ShellResize:
                return Route(context, viewerId, payload, payload.ShellResize.ShellId, closes: false, cancellationToken);
            case SessionPayload.BodyOneofCase.ShellExit:
                return Route(context, viewerId, payload, payload.ShellExit.ShellId, closes: true, cancellationToken);
            default:
                return inner.HandleAsync(context, viewerId, payload, cancellationToken);
        }
    }

    public async Task OnViewerReadyAsync(ISessionContext context, string viewerId, CancellationToken cancellationToken)
    {
        await ((ISessionChannelHandler)elevated).OnViewerReadyAsync(context, viewerId, cancellationToken).ConfigureAwait(false);
        await inner.OnViewerReadyAsync(context, viewerId, cancellationToken).ConfigureAwait(false);
    }

    public void OnViewerGone(string viewerId)
    {
        foreach (var key in _elevatedShells.Keys.Where(k => k.StartsWith(viewerId + "/", StringComparison.Ordinal)))
        {
            _elevatedShells.TryRemove(key, out _);
        }

        elevated.OnViewerGone(viewerId);
        inner.OnViewerGone(viewerId);
    }

    public ValueTask DisposeAsync() => elevated.DisposeAsync();

    private Task Route(ISessionContext context, string viewerId, SessionPayload payload, string shellId, bool closes, CancellationToken cancellationToken)
    {
        var key = Key(viewerId, shellId);
        if (!_elevatedShells.ContainsKey(key))
        {
            return inner.HandleAsync(context, viewerId, payload, cancellationToken);
        }

        if (closes)
        {
            _elevatedShells.TryRemove(key, out _);
        }

        return elevated.HandleAsync(context, viewerId, payload, cancellationToken);
    }

    private static string Key(string viewerId, string shellId) => viewerId + "/" + shellId;
}
