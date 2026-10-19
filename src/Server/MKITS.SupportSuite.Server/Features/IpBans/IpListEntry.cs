// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>Kind of a static IP list entry.</summary>
public enum IpListType
{
    /// <summary>Never banned, failures are not counted.</summary>
    Allow = 0,

    /// <summary>Always rejected.</summary>
    Deny = 1,
}

/// <summary>
/// Allow/deny list entry managed in the admin UI. Entries from configuration
/// (<c>Mkits:IpBan:AllowList</c> / <c>DenyList</c>) apply in addition and cannot be removed in the UI.
/// </summary>
public sealed class IpListEntry
{
    public Guid Id { get; set; }

    /// <summary>CIDR notation.</summary>
    public string Network { get; set; } = string.Empty;

    public IpListType ListType { get; set; }

    public string? Comment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Guid? CreatedByUserId { get; set; }
}
