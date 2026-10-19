// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Identity;
using MKITS.SupportSuite.Server.Infrastructure.Data;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// Registration of ASP.NET Core Identity (users, roles, password hashing, lockout).
/// Login UI, cookies and TOTP follow in Phase 4B.
/// </summary>
public static class IdentitySetup
{
    public static IServiceCollection AddMkitsIdentity(this IServiceCollection services)
    {
        services.AddIdentityCore<MkitsUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // Long passphrases over complexity rules (NIST SP 800-63B); breached-password checks may follow.
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
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<MkitsDbContext>();

        return services;
    }
}
