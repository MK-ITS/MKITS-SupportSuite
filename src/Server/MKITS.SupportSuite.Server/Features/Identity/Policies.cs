// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>Authorization policy names.</summary>
public static class Policies
{
    /// <summary>Any logged-in User or Admin.</summary>
    public const string Supporter = "Supporter";

    /// <summary>Admin role <b>and</b> two-factor authentication set up (mandatory for Admins).</summary>
    public const string Admin = "Admin";
}

/// <summary>Requires that the user has two-factor authentication enabled.</summary>
public sealed class TwoFactorEnrolledRequirement : IAuthorizationRequirement;

/// <summary>Checks <see cref="TwoFactorEnrolledRequirement"/> against the current database state (not the cookie).</summary>
public sealed class TwoFactorEnrolledHandler(UserManager<MkitsUser> userManager)
    : AuthorizationHandler<TwoFactorEnrolledRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, TwoFactorEnrolledRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
        if (user is not null && !user.IsDisabled && await userManager.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
