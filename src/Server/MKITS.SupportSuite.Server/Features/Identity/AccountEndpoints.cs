// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using MKITS.SupportSuite.Server.Features.Audit;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>Plain HTTP endpoints used by the UI: logout (POST, antiforgery-protected) and language switch.</summary>
public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapPost("/account/logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<MkitsUser> signInManager, UserManager<MkitsUser> userManager, IAuditLog auditLog) =>
            {
                // Minimal API endpoints without form binding are not validated automatically – check explicitly (CSRF).
                if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
                {
                    return Results.BadRequest();
                }

                var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
                await signInManager.SignOutAsync().ConfigureAwait(false);

                if (user is not null)
                {
                    await auditLog.WriteAsync(new AuditEntry(
                        AuditEventTypes.Logout, AuditActorType.User, ActorId: user.Id.ToString(),
                        Ip: context.Connection.RemoteIpAddress?.ToString())).ConfigureAwait(false);
                }

                return Results.LocalRedirect("/");
            })
            .DisableAntiforgery(); // validated explicitly above

        routes.MapGet("/culture/set", async (HttpContext context, string? culture, string? returnUrl, UserManager<MkitsUser> userManager) =>
        {
            if (CultureCookie.IsSupported(culture))
            {
                CultureCookie.Append(context, culture!);

                // Remember the choice in the profile of logged-in users.
                var user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
                if (user is not null)
                {
                    user.UiCulture = culture!.ToLowerInvariant();
                    await userManager.UpdateAsync(user).ConfigureAwait(false);
                }
            }

            return Results.LocalRedirect(LocalUrl.Sanitize(returnUrl));
        });

        return routes;
    }
}
