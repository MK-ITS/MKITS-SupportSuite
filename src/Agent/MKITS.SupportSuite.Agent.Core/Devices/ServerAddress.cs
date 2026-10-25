// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Core.Devices;

/// <summary>The server address an installed agent is configured with.</summary>
public static class ServerAddress
{
    /// <summary>
    /// Accepts https URLs; plain http only for <c>localhost</c> (development). Returns the normalized base address or <c>null</c>.
    /// </summary>
    public static Uri? Parse(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var allowed = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        return allowed ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/") : null;
    }
}
