// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Windows.Permanent;

/// <summary>Where the installed agent lives (ADR-0022).</summary>
internal static class AgentPaths
{
    public const string ServiceName = "MKITSAgent";

    public const string ServiceDisplayName = "MKITS SupportSuite Agent";

    public const string ExecutableName = "MKITS-Agent.exe";

    /// <summary><c>%ProgramFiles%\MKITS-SupportSuite\Agent</c> – writable for Administrators only (default ACL).</summary>
    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MKITS-SupportSuite", "Agent");

    public static string Executable => Path.Combine(InstallDirectory, ExecutableName);

    /// <summary><c>%ProgramData%\MKITS-SupportSuite\agent</c> – SYSTEM and Administrators only.</summary>
    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MKITS-SupportSuite", "agent");

    public static string ConfigFile => Path.Combine(DataDirectory, "agent.json");

    public static string IdentityFile => Path.Combine(DataDirectory, "identity.json");

    public static string LogFile => Path.Combine(DataDirectory, "agent.log");

    /// <summary>Downloaded, verified update waiting to be applied (ADR-0023).</summary>
    public static string UpdateDirectory => Path.Combine(DataDirectory, "update");

    public static string StagedUpdate => Path.Combine(UpdateDirectory, ExecutableName);

    /// <summary>The previous EXE, kept until the updated service runs.</summary>
    public static string BackupExecutable => Executable + ".previous";

    /// <summary>Configuration of one on-demand helper (pipe name, service PID).</summary>
    public static string HelperDirectory(string id) => Path.Combine(DataDirectory, "helper", id);
}
