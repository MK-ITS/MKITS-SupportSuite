// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Localization;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Sessions;

namespace MKITS.SupportSuite.Server.Features.Relay;

/// <summary>
/// The browser viewer page <c>/sessions/{id}/view</c> (Phase 9). Plain server-rendered HTML (no Blazor): the
/// TypeScript viewer (src/Viewer/web → wwwroot/viewer/viewer.js) needs a fresh one-time ticket, the session id and
/// the localized texts, which are rendered as data attributes and a JSON data block (no inline script, strict CSP).
/// </summary>
public static class ViewerPage
{
    public const string Path = "/sessions/{sessionId:guid}/view";

    /// <summary>Texts used by the viewer; keys are looked up in SharedResource (EN/DE).</summary>
    internal static readonly string[] TextKeys =
    [
        "Viewer_Connecting", "Viewer_Closed", "Viewer_SecurityCode", "Viewer_Monitor", "Viewer_Primary", "Viewer_ChatYou",
        "Viewer_ClipboardDenied",
        "Viewer_Status_WAITING_FOR_AGENT", "Viewer_Status_WAITING_FOR_VIEWER", "Viewer_Status_AWAITING_CONSENT",
        "Viewer_Status_ACTIVE", "Viewer_Status_SUSPENDED", "Viewer_Status_ENDED",
        "Viewer_Error_CONSENT_DENIED", "Viewer_Error_SESSION_ENDED", "Viewer_Error_AUTH_FAILED", "Viewer_Error_RATE_LIMITED",
        "Viewer_Error_KEY_EXCHANGE_FAILED",
        "Sessions_EndReason_code_expired", "Sessions_EndReason_revoked", "Sessions_EndReason_ended_by_supporter",
        "Sessions_EndReason_ended_by_customer", "Sessions_EndReason_consent_denied", "Sessions_EndReason_consent_timeout",
        "Sessions_EndReason_no_viewer", "Sessions_EndReason_connection_lost", "Sessions_EndReason_max_duration",
        "Sessions_EndReason_server_restart",
    ];

    public static IEndpointRouteBuilder MapViewerPage(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(Path, RenderAsync).RequireAuthorization(Policies.Supporter);
        return endpoints;
    }

    private static async Task<IResult> RenderAsync(
        Guid sessionId,
        HttpContext context,
        SessionService sessions,
        IAuthorizationService authorization,
        IStringLocalizer<SharedResource> localizer)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return Results.Forbid();
        }

        var isAdmin = (await authorization.AuthorizeAsync(context.User, Policies.Admin).ConfigureAwait(false)).Succeeded;
        var ticket = await sessions.IssueViewerTicketAsync(sessionId, userId, isAdmin, context.RequestAborted).ConfigureAwait(false);
        if (ticket is null)
        {
            return Results.Redirect("/sessions");
        }

        var displayName = await sessions.GetActiveUserDisplayNameAsync(userId, context.RequestAborted).ConfigureAwait(false) ?? string.Empty;

        // The page carries a one-time ticket: never cache it.
        context.Response.Headers.CacheControl = "no-store";
        return Results.Content(Render(sessionId, ticket, displayName, localizer), "text/html; charset=utf-8");
    }

    internal static string Render(Guid sessionId, string ticket, string displayName, IStringLocalizer localizer)
    {
        var html = HtmlEncoder.Default;
        string T(string key) => html.Encode(localizer[key]);

        // System.Text.Json escapes <, >, & and quotes by default, so the block cannot break out of its <script> element.
        var texts = JsonSerializer.Serialize(TextKeys.ToDictionary(k => k, k => localizer[k].Value));

        var page = new StringBuilder();
        page.Append(CultureInfo.InvariantCulture, $"""
            <!DOCTYPE html>
            <html lang="{html.Encode(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="referrer" content="no-referrer">
            <title>{T("Viewer_Title")}</title>
            <link rel="stylesheet" href="/css/app.css">
            </head>
            <body class="viewer-page">
            <div id="viewer" data-session-id="{html.Encode(sessionId.ToString())}" data-ticket="{html.Encode(ticket)}" data-ws="/ws/viewer" data-supporter="{html.Encode(displayName)}">
              <header class="viewer-toolbar">
                <a href="/sessions">{T("Viewer_Back")}</a>
                <span id="viewer-status" role="status" aria-live="polite"></span>
                <span id="viewer-code" class="security-code" title="{T("Viewer_SecurityCodeHint")}" hidden></span>
                <select id="viewer-monitor" aria-label="{T("Viewer_Monitor")}" hidden></select>
                <button type="button" class="secondary" data-special="CTRL_ALT_DEL">Ctrl+Alt+Del</button>
                <button type="button" class="secondary" data-special="WIN">Win</button>
                <button type="button" class="secondary" data-special="ALT_TAB">Alt+Tab</button>
                <button type="button" class="secondary" id="viewer-clipboard-send">{T("Viewer_ClipboardSend")}</button>
                <button type="button" class="secondary" id="viewer-clipboard-copy" hidden>{T("Viewer_ClipboardCopy")}</button>
                <button type="button" class="danger" id="viewer-end">{T("Sessions_Action_End")}</button>
              </header>
              <main class="viewer-main">
                <canvas id="viewer-canvas" aria-label="{T("Viewer_ScreenLabel")}"></canvas>
                <aside class="viewer-chat" aria-label="{T("Viewer_Chat")}">
                  <ol id="viewer-chat-log"></ol>
                  <form id="viewer-chat-form" autocomplete="off">
                    <input id="viewer-chat-input" maxlength="4000" aria-label="{T("Viewer_Chat")}">
                    <button type="submit">{T("Viewer_ChatSend")}</button>
                  </form>
                </aside>
              </main>
            </div>
            <script type="application/json" id="viewer-texts">{texts}</script>
            <script type="module" src="/viewer/viewer.js"></script>
            </body>
            </html>
            """);
        return page.ToString();
    }
}
