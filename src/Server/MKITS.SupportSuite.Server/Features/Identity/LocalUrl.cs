// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>Protection against open redirects: only same-site paths are accepted as return URLs.</summary>
public static class LocalUrl
{
    /// <summary>Returns <paramref name="url"/> if it is a local path (<c>/x</c>), otherwise <c>/</c>.</summary>
    public static string Sanitize(string? url)
    {
        if (string.IsNullOrEmpty(url) || url[0] != '/')
        {
            return "/";
        }

        // "//host" and "/\host" are protocol-relative URLs pointing to other sites.
        if (url.Length > 1 && (url[1] == '/' || url[1] == '\\'))
        {
            return "/";
        }

        return url.Any(char.IsControl) ? "/" : url;
    }
}
