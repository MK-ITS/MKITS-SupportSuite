// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>Where the installed Linux agent lives (ADR-0025). The package installs the program; these hold its state.</summary>
internal static class AgentPaths
{
    public const string ServiceName = "mkits-agent.service";

    /// <summary>Program folder of the <c>.deb</c> package (self-contained .NET app); <c>/usr/bin/mkits-agent</c> links here.</summary>
    public const string InstallDirectory = "/opt/mkits-agent";

    public const string Executable = InstallDirectory + "/mkits-agent";

    /// <summary>Configuration (server, enrollment token): root only (0700, files 0600).</summary>
    public const string ConfigDirectory = "/etc/mkits-agent";

    public const string ConfigFile = ConfigDirectory + "/agent.json";

    /// <summary>Device identity (private key): root only (0700, files 0600).</summary>
    public const string DataDirectory = "/var/lib/mkits-agent";

    public const string IdentityFile = DataDirectory + "/identity.json";

    /// <summary>Downloaded, verified package waiting to be installed (ADR-0023); root only like the data folder.</summary>
    public const string UpdateDirectory = DataDirectory + "/update";

    public const string StagedUpdate = UpdateDirectory + "/mkits-agent_amd64.deb";
}
