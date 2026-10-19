// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Microsoft.Extensions.Options;

namespace MKITS.SupportSuite.Server.Infrastructure.Configuration;

/// <summary>
/// Validates <see cref="MkitsOptions"/> at startup so misconfiguration fails fast with a clear message.
/// </summary>
public sealed class MkitsOptionsValidator : IValidateOptions<MkitsOptions>
{
    public ValidateOptionsResult Validate(string? name, MkitsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!Uri.TryCreate(options.PublicBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            failures.Add($"{MkitsOptions.SectionName}:PublicBaseUrl must be an absolute http(s) URL without query or fragment, e.g. https://support.example.com.");
        }

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add($"{MkitsOptions.SectionName}:DataDirectory must not be empty.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
