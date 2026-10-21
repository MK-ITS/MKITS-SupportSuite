// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Infrastructure.Security;

/// <summary>
/// Adds defensive HTTP response headers to every response (docs/security-concept.md, threat T5).
/// HSTS is set by the reverse proxy, which terminates TLS.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Strict default policy: only same-origin resources, no inline scripts, no framing.
    /// <c>connect-src 'self'</c> also covers same-origin WebSockets (wss:) in current browsers.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    /// <summary>
    /// <see cref="HttpContext.Items"/> key: an endpoint may store a stricter or specific policy for its response
    /// (used by the browser viewer page, see <c>ViewerPage</c>).
    /// </summary>
    public const string PolicyOverrideKey = "Mkits.ContentSecurityPolicy";

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.OnStarting(static state =>
        {
            var httpContext = (HttpContext)state;
            var headers = httpContext.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = httpContext.Items.TryGetValue(PolicyOverrideKey, out var policy) && policy is string custom
                ? custom
                : ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        }, context);

        return next(context);
    }
}
