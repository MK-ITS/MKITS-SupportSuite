// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Identity;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// A person who can log in: role <see cref="Roles.User"/> or <see cref="Roles.Admin"/>.
/// Guests (customers) are never stored. See docs/data-model.md.
/// </summary>
public sealed class MkitsUser : IdentityUser<Guid>
{
    /// <summary>Name shown to customers in consent dialogs and to other supporters.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Preferred UI culture (<c>en</c> or <c>de</c>); <c>null</c> = browser language.</summary>
    public string? UiCulture { get; set; }

    /// <summary>Disabled users cannot log in; their active sessions are ended.</summary>
    public bool IsDisabled { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }
}
