// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>
/// Rejects requests (including WebSocket upgrades) from banned or denied networks with a bare 403.
/// Runs directly after the forwarded-headers middleware, before authentication and endpoints.
/// No details are returned, so attackers learn nothing about ban duration or reason.
/// </summary>
public sealed class IpBanMiddleware(RequestDelegate next, IpBanService ipBanService)
{
    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!ipBanService.IsAllowed(context.Connection.RemoteIpAddress))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        return next(context);
    }
}
