// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Globalization;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;

namespace MKITS.SupportSuite.Agent.Linux.Terminal;

/// <summary>
/// Terminal user interface of the Linux ad-hoc client (Phase 11A; a graphical window follows): status lines, the
/// consent question on the terminal, chat messages and the security code. The session can always be ended with Ctrl+C.
/// </summary>
internal sealed class ConsoleUserInterface : IAgentUserInterface, IChatPresenter
{
    private readonly Lock _consoleGate = new();

    public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        return Task.Run(() =>
        {
            lock (_consoleGate)
            {
                Write(Text.Format("Consent_Question", prompt.SupporterDisplayName, prompt.OperatorName, prompt.ServerHost));
                Write(Text.Format("Consent_Permissions", string.Join(", ", prompt.Permissions.Select(p => Text.Get("Permission_" + p)))));
                Console.Write(Text.Get("Consent_Prompt") + " ");
                var answer = Console.ReadLine()?.Trim();
                return string.Equals(answer, Text.Get("Consent_YesLetter"), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase);
            }
        }, cancellationToken);
    }

    public void OnStatusChanged(AgentStatus status, string? detail) => Write(status switch
    {
        AgentStatus.Connecting => Text.Format("Status_Connecting", detail),
        AgentStatus.WaitingForSupporter => Text.Format("Status_WaitingForSupporter", detail),
        AgentStatus.AwaitingConsent => Text.Get("Status_AwaitingConsent"),
        AgentStatus.Active => Text.Get("Status_Active"),
        AgentStatus.Reconnecting => Text.Get("Status_Reconnecting"),
        _ => detail is not null && string.Equals(detail, detail.ToUpperInvariant(), StringComparison.Ordinal)
            ? Text.ForError(detail)
            : Text.Get("Status_Ended"),
    });

    public void OnSupportersChanged(IReadOnlyList<string> displayNames)
    {
        ArgumentNullException.ThrowIfNull(displayNames);
        if (displayNames.Count > 0)
        {
            Write(Text.Format("Supporters", string.Join(", ", displayNames)));
        }
    }

    public void OnSecurityCode(string supporterDisplayName, string securityCode) =>
        Write(Text.Format("SecurityCode", supporterDisplayName, securityCode));

    public void ShowMessage(string author, string text, DateTimeOffset timestamp) =>
        Write($"[{timestamp.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}] {author}: {text}");

    private static void Write(string line) => Console.WriteLine(line);
}
