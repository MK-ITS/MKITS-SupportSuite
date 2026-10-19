// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Audit;

/// <summary>Registration of the audit log feature.</summary>
public static class AuditSetup
{
    public static IServiceCollection AddMkitsAudit(this IServiceCollection services)
    {
        // Singleton: one writer lock for the whole process keeps the hash chain linear.
        services.AddSingleton<IAuditLog, AuditLog>();
        return services;
    }
}
