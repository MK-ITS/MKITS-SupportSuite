// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using System.Text.RegularExpressions;

namespace MKITS.SupportSuite.Common.Sessions;

/// <summary>Server address and session code carried in the ad-hoc client's file name.</summary>
public sealed record AdHocClientTarget(string Host, int? Port, string Code);

/// <summary>
/// File name scheme of the ad-hoc client (ADR-0019): <c>MKITS-Support_&lt;host&gt;[+&lt;port&gt;]_&lt;code&gt;.&lt;ext&gt;</c>,
/// e.g. <c>MKITS-Support_support.example.com_12345678.exe</c>. Used by the portal (build) and the clients (parse).
/// A port is written with <c>+</c> because <c>:</c> is not allowed in Windows file names.
/// </summary>
public static partial class AdHocClientFileName
{
    public const string Prefix = "MKITS-Support";

    /// <summary>
    /// Builds the download file name. Returns the plain <c>MKITS-Support&lt;ext&gt;</c> if the host cannot be
    /// encoded (e.g. an IPv6 literal); the client then asks for server and code.
    /// </summary>
    public static string Build(string host, int? port, string code, string extension)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(extension);

        if (!IsValidHost(host) || SessionCodeFormat.Normalize(code) is not { } normalized || port is < 1 or > 65535)
        {
            return Prefix + extension;
        }

        var portPart = port is { } p ? "+" + p.ToString(CultureInfo.InvariantCulture) : string.Empty;
        return $"{Prefix}_{host.ToLowerInvariant()}{portPart}_{normalized}{extension}";
    }

    /// <summary>
    /// Parses a file name (with or without directory). Tolerates browser suffixes such as <c> (1)</c> and any
    /// extension. Returns <c>null</c> if the name does not follow the scheme.
    /// </summary>
    public static AdHocClientTarget? TryParse(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        var match = NamePattern().Match(name);
        if (!match.Success)
        {
            return null;
        }

        var host = match.Groups["host"].Value.ToLowerInvariant();
        int? port = null;
        if (match.Groups["port"].Success)
        {
            if (!int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 or > 65535)
            {
                return null;
            }

            port = value;
        }

        return IsValidHost(host) ? new AdHocClientTarget(host, port, match.Groups["code"].Value) : null;
    }

    /// <summary>DNS name or IPv4 address: labels of 1–63 letters, digits or hyphens, not starting or ending with a hyphen.</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrEmpty(host) || host.Length > 253)
        {
            return false;
        }

        foreach (var label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-' || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    // MKITS-Support_<host>[+<port>]_<code>[ (n)|(n)|-n][.ext]
    [GeneratedRegex(@"^MKITS-Support_(?<host>[A-Za-z0-9.-]+)(\+(?<port>[0-9]{1,5}))?_(?<code>[0-9]{6,12})(\s?\([0-9]{1,3}\))?(\.[A-Za-z0-9]{1,10})?$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex NamePattern();
}
