// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using MKITS.SupportSuite.Server.Features.Devices;

namespace MKITS.SupportSuite.Server.Features.Sessions;

/// <summary>What a redeemed viewer ticket grants: joining one session as one user with the given permissions.</summary>
public sealed record ViewerTicketGrant(Guid SessionId, Guid UserId, DevicePermissions Permissions = DevicePermissions.All);

/// <summary>
/// One-time, short-lived tickets for the viewer WebSocket (docs/protocol.md §3). The web session (cookie) never
/// reaches the WebSocket; the viewer exchanges a ticket instead. Kept in memory only – a restart ends all
/// sessions anyway. Only hashes are stored.
/// </summary>
public sealed class ViewerTicketStore(TimeProvider timeProvider)
{
    /// <summary>Upper limit of outstanding tickets; protects memory if a logged-in user requests tickets in a loop.</summary>
    internal const int MaxTickets = 10_000;

    private readonly ConcurrentDictionary<string, Entry> _tickets = new(StringComparer.Ordinal);

    public int Count => _tickets.Count;

    /// <summary>Creates a ticket (256 random bits, base64url).</summary>
    public string Issue(Guid sessionId, Guid userId, TimeSpan lifetime, DevicePermissions permissions = DevicePermissions.All)
    {
        var now = timeProvider.GetUtcNow();
        if (_tickets.Count >= MaxTickets)
        {
            Purge(now);
        }

        if (_tickets.Count >= MaxTickets)
        {
            throw new InvalidOperationException("Too many outstanding viewer tickets.");
        }

        var ticket = Base64Url(RandomNumberGenerator.GetBytes(32));
        _tickets[Hash(ticket)] = new Entry(new ViewerTicketGrant(sessionId, userId, permissions), now + lifetime);
        return ticket;
    }

    /// <summary>Consumes a ticket. Returns <c>null</c> if it is unknown, already used or expired.</summary>
    public ViewerTicketGrant? Redeem(string? ticket)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 100)
        {
            return null;
        }

        if (!_tickets.TryRemove(Hash(ticket), out var entry))
        {
            return null;
        }

        return entry.ExpiresAt > timeProvider.GetUtcNow() ? entry.Grant : null;
    }

    /// <summary>Removes expired tickets.</summary>
    public void Purge(DateTimeOffset now)
    {
        foreach (var (key, entry) in _tickets)
        {
            if (entry.ExpiresAt <= now)
            {
                _tickets.TryRemove(key, out _);
            }
        }
    }

    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Entry(ViewerTicketGrant Grant, DateTimeOffset ExpiresAt);
}
