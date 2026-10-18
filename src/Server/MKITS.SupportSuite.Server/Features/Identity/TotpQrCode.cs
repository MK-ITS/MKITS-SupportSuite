// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Text;
using QRCoder;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>Builds the otpauth:// URI (RFC 6238 key URI format) and its QR code for authenticator apps.</summary>
public static class TotpQrCode
{
    /// <summary>Issuer shown in the authenticator app, e.g. "MKITS (support.example.com)" – distinguishes several servers.</summary>
    public static string IssuerFor(string publicBaseUrl) =>
        Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var uri) ? $"MKITS ({uri.Host})" : "MKITS";

    public static string BuildUri(string issuer, string account, string key)
    {
        var encodedIssuer = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{encodedIssuer}:{Uri.EscapeDataString(account)}?secret={key}&issuer={encodedIssuer}&digits=6&period=30";
    }

    /// <summary>The key in groups of four for manual entry, e.g. "abcd efgh …".</summary>
    public static string FormatKey(string key)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }

        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>QR code as SVG data URI (allowed by the CSP via <c>img-src data:</c>; no script, no external service).</summary>
    public static string CreateSvgDataUri(string content)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
        using var svg = new SvgQRCode(data);
        var markup = svg.GetGraphic(5);
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(markup));
    }
}
