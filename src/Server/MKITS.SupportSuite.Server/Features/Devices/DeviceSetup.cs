// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Devices;

/// <summary>Registration of the permanent-device feature (groups, permissions, enrollment, registry).</summary>
public static class DeviceSetup
{
    public static IServiceCollection AddMkitsDevices(this IServiceCollection services)
    {
        services.AddSingleton<DeviceService>();
        return services;
    }
}
