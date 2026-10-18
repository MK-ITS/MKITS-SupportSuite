// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// Sign-in manager that additionally refuses disabled users (<see cref="MkitsUser.IsDisabled"/>).
/// This also applies when an existing cookie is revalidated, so disabling a user logs them out everywhere.
/// </summary>
public sealed class MkitsSignInManager(
    UserManager<MkitsUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<MkitsUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<MkitsUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<MkitsUser> confirmation)
    : SignInManager<MkitsUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    public override async Task<bool> CanSignInAsync(MkitsUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return !user.IsDisabled && await base.CanSignInAsync(user).ConfigureAwait(false);
    }
}
