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
}
