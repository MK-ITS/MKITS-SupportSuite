// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Localization;
using MKITS.SupportSuite.Server.Infrastructure.Localization;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>Writes the UI language cookie read by <see cref="CookieRequestCultureProvider"/>.</summary>
public static class CultureCookie
{
    public static bool IsSupported(string? culture) =>
        culture is not null && LocalizationSetup.SupportedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase);

    public static void Append(HttpContext context, string culture)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture.ToLowerInvariant())),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                IsEssential = true,
                SameSite = SameSiteMode.Lax,
                Secure = context.Request.IsHttps,
                Path = "/",
            });
    }
}
