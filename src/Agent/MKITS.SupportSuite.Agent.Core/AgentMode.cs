// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Core;

/// <summary>
/// Operating mode of an agent.
/// </summary>
public enum AgentMode
{
    /// <summary>
    /// One-time support session: started by the end user after entering a session code,
    /// runs without installation, terminates when the session ends. Connections always require user consent.
    /// </summary>
    AdHoc = 0,

    /// <summary>
    /// Installed agent (Windows service / systemd unit / Android app) enrolled into a device group by an Admin.
    /// Allows unattended access according to the group's consent policy.
    /// </summary>
    Permanent = 1,
}
