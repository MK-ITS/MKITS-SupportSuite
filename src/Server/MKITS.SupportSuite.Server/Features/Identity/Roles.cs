// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// Stored roles. "Guest" is not a role: it is any unauthenticated visitor of the customer portal.
/// </summary>
public static class Roles
{
    /// <summary>Supporter: may create ad-hoc sessions and connect to devices of permitted groups.</summary>
    public const string User = "User";

    /// <summary>Everything a User may do, plus administration and permanent connections.</summary>
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All = [User, Admin];
}
