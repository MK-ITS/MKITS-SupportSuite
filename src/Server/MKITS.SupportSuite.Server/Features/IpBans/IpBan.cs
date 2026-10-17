// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>
/// A blocked network (single address = /32 or /128; IPv6 clients are banned per /64 by default).
/// Created automatically by <see cref="IpBanService"/> or manually by an Admin.
/// </summary>
public sealed class IpBan
{
    public Guid Id { get; set; }

    /// <summary>CIDR notation, e.g. <c>203.0.113.7/32</c> or <c>2001:db8:1:2::/64</c>.</summary>
    public string Network { get; set; } = string.Empty;

    /// <summary><c>auto:&lt;failure kind&gt;</c> for automatic bans, otherwise the Admin's comment.</summary>
    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary><c>null</c> = permanent.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary><c>null</c> = created automatically.</summary>
    public Guid? CreatedByUserId { get; set; }

    public DateTimeOffset? LiftedAt { get; set; }

    public Guid? LiftedByUserId { get; set; }

    /// <summary>A ban is active if it was not lifted and has not expired.</summary>
    public bool IsActiveAt(DateTimeOffset now) => LiftedAt is null && (ExpiresAt is null || ExpiresAt > now);
}
