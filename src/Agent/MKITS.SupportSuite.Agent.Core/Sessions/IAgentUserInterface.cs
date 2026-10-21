// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

namespace MKITS.SupportSuite.Agent.Core.Sessions;

/// <summary>Connection state shown in the agent's window / banner.</summary>
public enum AgentStatus
{
    Connecting,
    WaitingForSupporter,
    AwaitingConsent,
    Active,
    Reconnecting,
    Ended,
}

/// <summary>Everything the end user needs to decide whether to allow a supporter.</summary>
public sealed record ConsentPrompt(string ServerHost, string OperatorName, string SupporterDisplayName, IReadOnlyList<string> Permissions);

/// <summary>
/// The end user's side of the session, implemented by the platform UI (Windows dialog, Linux GTK, Android
/// activity). Calls may arrive on any thread.
/// </summary>
public interface IAgentUserInterface
{
    /// <summary>Asks the end user. Must return <c>false</c> when the user closes the dialog. The server times out after 2 minutes.</summary>
    Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken);

    void OnStatusChanged(AgentStatus status, string? detail);

    /// <summary>A supporter joined or left (for the banner: who is connected).</summary>
    void OnSupportersChanged(IReadOnlyList<string> displayNames);

    /// <summary>The end-to-end security code for a supporter (to compare on the phone).</summary>
    void OnSecurityCode(string supporterDisplayName, string securityCode);
}

/// <summary>Handles decrypted session-plane payloads of the channels it is responsible for.</summary>
public interface ISessionChannelHandler
{
    /// <summary>Called for every decrypted payload; ignore those that are not for this handler.</summary>
    Task HandleAsync(ISessionContext context, string viewerId, Common.Protocol.V0.SessionPayload payload, CancellationToken cancellationToken);

    /// <summary>A viewer finished the key exchange and can receive payloads (e.g. start streaming, send the monitor list).</summary>
    Task OnViewerReadyAsync(ISessionContext context, string viewerId, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>A viewer left or its keys were discarded (e.g. release pressed keys, stop streaming to it).</summary>
    void OnViewerGone(string viewerId)
    {
    }
}

/// <summary>Lets channel handlers send payloads back (encrypted per viewer by the session).</summary>
public interface ISessionContext
{
    /// <summary>Sends to one viewer, or to all viewers with an established key when <paramref name="viewerId"/> is <c>null</c>.</summary>
    Task SendAsync(string? viewerId, Common.Protocol.V0.SessionPayload payload, CancellationToken cancellationToken);

    /// <summary>Viewers that finished the key exchange (can receive payloads right now).</summary>
    IReadOnlyCollection<string> ReadyViewerIds { get; }
}
