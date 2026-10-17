// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Common.Protocol;

namespace MKITS.SupportSuite.Server.Endpoints;

/// <summary>
/// Unauthenticated system endpoints (version information). Health is mapped separately at <c>/health</c>.
/// </summary>
public static class SystemEndpoints
{
    /// <summary>Response of <c>GET /api/system/version</c>.</summary>
    public sealed record VersionResponse(string Product, string Version, string ProtocolVersion, string License, string SourceCodeUrl);

    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/api/system/version", () => new VersionResponse(
            ProductInfo.Name,
            ProductInfo.Version,
            ProtocolVersion.AsString,
            ProductInfo.License,
            ProductInfo.SourceCodeUrl));

        return routes;
    }
}
