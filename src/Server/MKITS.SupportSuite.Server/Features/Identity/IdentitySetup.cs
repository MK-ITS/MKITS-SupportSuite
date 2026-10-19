// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// Registration of ASP.NET Core Identity (users, roles, password hashing, lockout, TOTP), authentication cookies
/// and authorization policies. See docs/security-concept.md §4.
/// </summary>
public static class IdentitySetup
{
    public static IServiceCollection AddMkitsIdentity(this IServiceCollection services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        services.AddAuthentication(options =>
            {
                options.DefaultScheme = IdentityConstants.ApplicationScheme;
                options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddIdentityCookies();

        services.AddIdentityCore<MkitsUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // Long passphrases over complexity rules (NIST SP 800-63B).
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = false;
                options.Password.RequireLowercase = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredUniqueChars = 4;

                // Per-account lockout complements the per-IP ban service (protects against distributed guessing).
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 10;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<MkitsDbContext>()
            .AddSignInManager<MkitsSignInManager>()
            .AddErrorDescriber<LocalizedIdentityErrorDescriber>()
            .AddDefaultTokenProviders();

        // Changes to a user (disable, password, 2FA reset) invalidate existing cookies within a minute.
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.FromMinutes(1));

        var secureCookies = environment.IsProduction();
        services.ConfigureApplicationCookie(options =>
        {
            // "__Host-" requires Secure + path "/" + no domain: the cookie cannot be set or overwritten by sub-domains.
            options.Cookie.Name = secureCookies ? "__Host-MKITS.Auth" : "MKITS.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = true;
            options.LoginPath = "/account/login";
            options.LogoutPath = "/account/logout";
            options.AccessDeniedPath = "/account/access-denied";
        });
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = secureCookies ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Supporter, policy => policy.RequireRole(Roles.User, Roles.Admin))
            .AddPolicy(Policies.Admin, policy => policy
                .RequireRole(Roles.Admin)
                .AddRequirements(new TwoFactorEnrolledRequirement()));
        services.AddScoped<IAuthorizationHandler, TwoFactorEnrolledHandler>();

        // Blazor: authentication state for components, revalidated periodically for interactive circuits.
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

        // Client address for audit entries written from interactive components.
        services.AddHttpContextAccessor();
        services.AddScoped<ClientConnectionInfo>();
        services.AddScoped<CircuitHandler, ClientConnectionCircuitHandler>();

        services.AddSingleton<SetupTokenService>();
        services.AddScoped<LoginCompletion>();
        services.AddScoped<UserAdministration>();

        return services;
    }
}
