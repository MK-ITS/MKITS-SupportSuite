// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// Log of the installed agent: standard error, which systemd stores in the journal (<c>journalctl -u mkits-agent</c>)
/// with timestamps. Never log secrets (tokens, keys, session payloads).
/// </summary>
internal static class AgentLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch (IOException)
            {
                // logging must never break the agent
            }
        }
    }
}
