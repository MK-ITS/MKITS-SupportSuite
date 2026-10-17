// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using Microsoft.AspNetCore.Localization;

namespace MKITS.SupportSuite.Server.Infrastructure.Localization;

/// <summary>
/// UI languages of the product (ADR-0000): English (default) and German.
/// </summary>
/// <remarks>
/// Culture resolution order: culture cookie (set when the user picks a language or logs in with a profile language),
/// then the browser's <c>Accept-Language</c> header, then English.
/// A provider for the user profile language is added with the identity feature (Phase 4).
/// </remarks>
public static class LocalizationSetup
{
    public const string DefaultCulture = "en";

    public static readonly IReadOnlyList<string> SupportedCultures = ["en", "de"];

    public static IServiceCollection AddMkitsLocalization(this IServiceCollection services)
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");

        services.Configure<RequestLocalizationOptions>(options =>
        {
            var cultures = SupportedCultures.Select(CultureInfo.GetCultureInfo).ToList();

            options.DefaultRequestCulture = new RequestCulture(DefaultCulture);
            options.SupportedCultures = cultures;
            options.SupportedUICultures = cultures;
            options.FallBackToParentCultures = true;
            options.FallBackToParentUICultures = true;
            options.RequestCultureProviders =
            [
                new CookieRequestCultureProvider(),
                new AcceptLanguageHeaderRequestCultureProvider(),
            ];
        });

        return services;
    }
}
