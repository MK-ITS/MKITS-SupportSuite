// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Reflection;

namespace MKITS.SupportSuite.Common;

/// <summary>
/// Product-wide metadata shared by all components (server, agents, viewers).
/// </summary>
public static class ProductInfo
{
    /// <summary>Display name of the product.</summary>
    public const string Name = "MKITS-SupportSuite";

    /// <summary>Public source code location (AGPL-3.0 section 13: users interacting over a network must be offered the source).</summary>
    public const string SourceCodeUrl = "https://github.com/MK-ITS/MKITS-SupportSuite";

    /// <summary>SPDX license identifier of the product.</summary>
    public const string License = "AGPL-3.0-only";

    /// <summary>
    /// Informational version of the product (e.g. <c>0.1.0-dev+abc123</c>), read from the shared assembly.
    /// All components are versioned together via <c>Directory.Build.props</c>.
    /// </summary>
    public static string Version { get; } =
        typeof(ProductInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// Orders builds for the agent auto-update (CI: commit timestamp). <c>0</c> for local builds, which are never offered
    /// as updates and never update themselves.
    /// </summary>
    public static long BuildNumber { get; } =
        long.TryParse(Metadata("BuildNumber"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;

    /// <summary>Public key (SPKI, ECDSA P-256) that signs agent updates, or <c>null</c> if this build has none (ADR-0023).</summary>
    public static byte[]? AgentUpdatePublicKey { get; } = DecodeKey(Metadata("AgentUpdatePublicKey"));

    private static string? Metadata(string key) =>
        typeof(ProductInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    private static byte[]? DecodeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
