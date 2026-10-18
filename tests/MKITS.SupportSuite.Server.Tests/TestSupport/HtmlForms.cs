// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Text.RegularExpressions;

namespace MKITS.SupportSuite.Server.Tests.TestSupport;

/// <summary>
/// Submits statically rendered Blazor forms like a browser: loads the page, takes the antiforgery token
/// and posts the fields together with the form name (<c>_handler</c>).
/// </summary>
public static partial class HtmlForms
{
    public static async Task<HttpResponseMessage> SubmitAsync(
        HttpClient client,
        string url,
        string formName,
        IReadOnlyDictionary<string, string> fields,
        string? remoteIp = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(fields);

        using var page = await SendAsync(client, HttpMethod.Get, url, remoteIp, content: null);
        var html = await page.Content.ReadAsStringAsync();
        var token = AntiforgeryToken(html)
            ?? throw new InvalidOperationException($"No antiforgery token found on {url} (status {(int)page.StatusCode}).");

        var form = new Dictionary<string, string>(fields)
        {
            ["_handler"] = formName,
            ["__RequestVerificationToken"] = token,
        };

        using var content = new FormUrlEncodedContent(form);
        return await SendAsync(client, HttpMethod.Post, url, remoteIp, content);
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, string? remoteIp, HttpContent? content)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var request = new HttpRequestMessage(method, new Uri(url, UriKind.Relative)) { Content = content };
        if (remoteIp is not null)
        {
            request.Headers.Add(MkitsWebApplicationFactory.TestRemoteIpHeader, remoteIp);
        }

        return await client.SendAsync(request);
    }

    /// <summary>Path and query of a redirect, whether the server sent an absolute or a relative Location.</summary>
    public static string? RedirectTarget(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var location = response.Headers.Location;
        return location is null ? null : location.IsAbsoluteUri ? location.PathAndQuery : location.OriginalString;
    }

    public static string? AntiforgeryToken(string html)
    {
        var match = TokenRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    [GeneratedRegex(@"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""(?<value>[^""]*)""|<input[^>]*value=""(?<value>[^""]*)""[^>]*name=""__RequestVerificationToken""", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
}
