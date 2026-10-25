// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Common.Sessions;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Infrastructure.Configuration;

namespace MKITS.SupportSuite.Server.Features.Downloads;

/// <summary>Customer portal endpoints (docs/session-lifecycle.md §5).</summary>
public static class PortalEndpoints
{
    public const string DownloadPath = "/portal/download";

    /// <summary>The Windows client as installable agent (<c>MKITS-Agent.exe</c>) for Admins (ADR-0022).</summary>
    public const string AgentDownloadPath = "/admin/downloads/windows-agent";

    /// <summary>The installed Linux agent as Debian package for Admins (ADR-0025).</summary>
    public const string LinuxAgentDownloadPath = "/admin/downloads/linux-agent";

    /// <summary>File name of the Linux agent package on download.</summary>
    public const string LinuxAgentFileName = "mkits-agent_amd64.deb";

    public static IServiceCollection AddMkitsDownloads(this IServiceCollection services)
    {
        services.AddSingleton<ClientCatalog>();
        return services;
    }

    public static IEndpointRouteBuilder MapPortalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // POST (not GET): the code must not end up in proxy access logs or the browser history.
        // Antiforgery is validated automatically for form-bound minimal API endpoints.
        endpoints.MapPost(DownloadPath, DownloadAsync);

        // Same binary; the file name carries no code, so the installed agent never mistakes itself for an ad-hoc client.
        endpoints.MapGet(AgentDownloadPath, (HttpContext context, ClientCatalog catalog) =>
        {
            if (catalog.Get(ClientPlatform.WindowsX64) is not { } client)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.File(client.FullPath, "application/octet-stream", "MKITS-Agent.exe");
        }).RequireAuthorization(Identity.Policies.Admin);

        endpoints.MapGet(LinuxAgentDownloadPath, (HttpContext context, ClientCatalog catalog) =>
        {
            if (catalog.FindFile(ClientCatalog.LinuxAgentPackage) is not { } package)
            {
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.File(package.FullName, "application/vnd.debian.binary-package", LinuxAgentFileName);
        }).RequireAuthorization(Identity.Policies.Admin);

        // Agent auto-update (ADR-0023). Public: the files are not secret (open source, same as the portal download) and
        // integrity comes from the release signature that agents verify, not from the server.
        endpoints.MapGet("/api/agent-updates/{platform}", (string platform, ClientCatalog catalog) =>
            ClientCatalog.FromFolder(platform) is { } p && catalog.GetUpdate(p) is { } update
                ? Results.Ok(new { manifest = Convert.ToBase64String(update.Manifest), signature = update.Signature })
                : Results.NotFound());
        endpoints.MapGet("/api/agent-updates/{platform}/file", (string platform, ClientCatalog catalog) =>
            ClientCatalog.FromFolder(platform) is { } p && catalog.GetUpdate(p) is { } update
                ? Results.File(update.Client.FullPath, "application/octet-stream", Path.GetFileName(update.Client.FullPath))
                : Results.NotFound());
        return endpoints;
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        [FromForm] string? code,
        [FromForm] string? platform,
        SessionService sessions,
        ClientCatalog catalog,
        IOptions<MkitsOptions> options)
    {
        if (!Enum.TryParse<ClientPlatform>(platform, ignoreCase: false, out var clientPlatform) || !Enum.IsDefined(clientPlatform))
        {
            return Results.BadRequest();
        }

        var check = await sessions.CheckCodeAsync(code, context.Connection.RemoteIpAddress, context.RequestAborted).ConfigureAwait(false);
        if (check != CodeCheckResult.Valid)
        {
            return Results.Redirect(check == CodeCheckResult.Expired ? "/?error=expired" : "/?error=invalid");
        }

        if (catalog.Get(clientPlatform) is not { } client)
        {
            return Results.NotFound();
        }

        var baseUri = new Uri(options.Value.PublicBaseUrl);
        var fileName = client.Package.CodeInFileName
            ? AdHocClientFileName.Build(baseUri.Host, baseUri.IsDefaultPort ? null : baseUri.Port, SessionCodeFormat.Normalize(code)!, client.Package.Extension)
            : AdHocClientFileName.Prefix + client.Package.Extension;

        context.Response.Headers.CacheControl = "no-store";
        return Results.File(client.FullPath, "application/octet-stream", fileName);
    }
}
