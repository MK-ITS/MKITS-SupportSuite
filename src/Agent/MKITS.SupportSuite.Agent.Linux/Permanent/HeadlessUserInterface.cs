// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Sessions;

namespace MKITS.SupportSuite.Agent.Linux.Permanent;

/// <summary>
/// The service's own "user interface" when no session window can be shown (nobody logged on graphically, Wayland,
/// headless server): there is nobody to ask, so a consent request is declined; everything else goes to the log.
/// Sessions with the "notify" or "silent" policy work without it.
/// </summary>
internal sealed class HeadlessUserInterface : IAgentUserInterface
{
    public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        AgentLog.Write($"consent requested by {prompt.SupporterDisplayName}, but nobody can be asked – declined");
        return Task.FromResult(false);
    }

    public void OnStatusChanged(AgentStatus status, string? detail) => AgentLog.Write($"session status: {status}");

    public void OnSupportersChanged(IReadOnlyList<string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(displayNames);
        AgentLog.Write($"supporters: {displayNames.Count}");
    }

    public void OnSecurityCode(string supporterDisplayName, string securityCode)
    {
        // nobody to show it to; never logged
    }
}
