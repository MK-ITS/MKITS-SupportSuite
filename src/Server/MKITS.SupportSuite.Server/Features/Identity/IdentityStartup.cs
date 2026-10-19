// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Identity;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>Startup tasks of the identity feature.</summary>
public static class IdentityStartup
{
    /// <summary>Creates the roles if missing and activates the setup token while no active Admin exists.</summary>
    public static async Task InitializeIdentityAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        await using var scope = app.Services.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role).ConfigureAwait(false))
            {
                await roleManager.CreateAsync(new IdentityRole<Guid>(role) { Id = Guid.CreateVersion7() }).ConfigureAwait(false);
            }
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var admins = await userManager.GetUsersInRoleAsync(Roles.Admin).ConfigureAwait(false);
        if (!admins.Any(a => !a.IsDisabled))
        {
            app.Services.GetRequiredService<SetupTokenService>().Activate();
        }
    }
}
