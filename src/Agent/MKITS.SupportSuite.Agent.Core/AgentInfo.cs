// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Common.Protocol;

namespace MKITS.SupportSuite.Agent.Core;

/// <summary>
/// Describes the running agent build. Platform projects pass their platform name.
/// </summary>
public static class AgentInfo
{
    /// <summary>Returns a one-line banner such as <c>MKITS-SupportSuite Agent (windows) 0.1.0-dev, protocol 0.1</c>.</summary>
    public static string GetBanner(string platform) =>
        $"{ProductInfo.Name} Agent ({platform}) {ProductInfo.Version}, protocol {ProtocolVersion.AsString}";
}
