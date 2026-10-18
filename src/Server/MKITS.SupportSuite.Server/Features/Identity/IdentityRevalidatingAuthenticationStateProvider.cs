// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// For interactive (circuit-based) components: re-checks every minute that the user still exists, is not disabled
/// and that the security stamp is unchanged (password reset, 2FA reset, disable). Otherwise the circuit loses its
/// authentication immediately instead of at the next page load.
/// </summary>
public sealed class IdentityRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authenticationState);

        await using var scope = scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        return await IsStillValidAsync(userManager, authenticationState.User).ConfigureAwait(false);
    }

    private async Task<bool> IsStillValidAsync(UserManager<MkitsUser> userManager, ClaimsPrincipal principal)
    {
        var user = await userManager.GetUserAsync(principal).ConfigureAwait(false);
        if (user is null || user.IsDisabled)
        {
            return false;
        }

        var principalStamp = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        var userStamp = await userManager.GetSecurityStampAsync(user).ConfigureAwait(false);
        return string.Equals(principalStamp, userStamp, StringComparison.Ordinal);
    }
}
