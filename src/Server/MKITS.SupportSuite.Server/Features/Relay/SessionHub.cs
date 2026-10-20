// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Server.Features.Audit;
using MKITS.SupportSuite.Server.Features.Sessions;
using SessionOptions = MKITS.SupportSuite.Server.Features.Sessions.SessionOptions;
using SessionStatus = MKITS.SupportSuite.Server.Features.Sessions.SessionStatus;
using WireStatus = MKITS.SupportSuite.Common.Protocol.V0.SessionStatus;

namespace MKITS.SupportSuite.Server.Features.Relay;

/// <summary>
/// The session broker (docs/session-lifecycle.md §3, docs/protocol.md §4–5): attaches authenticated peers to
/// sessions, drives the state machine (consent, suspend/resume, timeouts), and routes session-plane frames.
/// The server never decrypts <c>SessionData</c>; it only reads the routing header.
/// </summary>
public sealed partial class SessionHub : IDisposable
{
    /// <summary>Upper limit of supporters watching one session.</summary>
    internal const int MaxViewersPerSession = 5;

    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);

    private static readonly string[] RequestedPermissions = ["view", "control", "files", "shell"];

    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();
    private readonly ConcurrentDictionary<string, LiveSession> _sessionByPeer = new(StringComparer.Ordinal);
    private readonly SessionService _sessionService;
    private readonly IOptionsMonitor<SessionOptions> _options;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(SessionService sessionService, IOptionsMonitor<SessionOptions> options, IAuditLog auditLog, TimeProvider timeProvider, ILogger<SessionHub> logger)
    {
        _sessionService = sessionService;
        _options = options;
        _auditLog = auditLog;
        _timeProvider = timeProvider;
        _logger = logger;
        _sessionService.SessionEnded += OnSessionEndedInDatabase;
    }

    /// <summary>Number of sessions with at least one connected peer.</summary>
    public int LiveSessionCount => _sessions.Count;

    /// <summary>Live status of a session as seen by the relay (for tests and the supporter UI).</summary>
    public SessionStatus? GetLiveStatus(Guid sessionId) => _sessions.TryGetValue(sessionId, out var live) ? live.Status : null;

    // ------------------------------------------------------------------ attach

    /// <summary>
    /// Binds an agent that just redeemed a code (or resumed) to its session and sends <c>AuthResult</c> and
    /// <c>SessionAssigned</c>. Pending viewers are asked for consent now.
    /// </summary>
    public async Task<bool> AttachAgentAsync(PeerConnection agent, Guid sessionId, string? operatorName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var facts = await _sessionService.GetFactsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (facts is null || facts.Status == SessionStatus.Ended)
        {
            agent.Send(ErrorFrame(ErrorCodes.SessionEnded, "session ended"));
            return false;
        }

        var live = _sessions.GetOrAdd(sessionId, _ => new LiveSession(facts.Id, facts.Kind, facts.OwnerUserId, facts.Status, _timeProvider.GetUtcNow()));
        await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (live.IsEnded || live.Agent is not null)
            {
                agent.Send(ErrorFrame(ErrorCodes.SessionEnded, "session not available"));
                return false;
            }

            live.OperatorName = operatorName ?? live.OperatorName;
            await ConnectAgentLockedAsync(live, agent, resumed: false, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <summary>Re-attaches an agent within the grace period using its one-time resume token.</summary>
    public async Task<bool> ResumeAgentAsync(PeerConnection agent, string? sessionId, string? token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (!Guid.TryParse(sessionId, out var id) || string.IsNullOrEmpty(token) || !_sessions.TryGetValue(id, out var live))
        {
            return false;
        }

        await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (live.IsEnded || live.ResumeTokenHash is null || !CryptographicOperations.FixedTimeEquals(live.ResumeTokenHash, HashToken(token)))
            {
                return false;
            }

            if (live.Agent is { } stale)
            {
                // The agent noticed the connection loss before the server did: the token proves it is the same
                // agent, so the old connection is dropped and replaced.
                _sessionByPeer.TryRemove(stale.Id, out _);
                live.Agent = null;
                stale.Abort();
            }
            else if (live.Status != SessionStatus.Suspended)
            {
                return false;
            }

            await ConnectAgentLockedAsync(live, agent, resumed: true, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <summary>Binds a supporter (redeemed viewer ticket) to the session.</summary>
    public async Task<bool> AttachViewerAsync(PeerConnection viewer, ViewerTicketGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(grant);

        var facts = await _sessionService.GetFactsAsync(grant.SessionId, cancellationToken).ConfigureAwait(false);
        var displayName = await _sessionService.GetActiveUserDisplayNameAsync(grant.UserId, cancellationToken).ConfigureAwait(false);
        if (facts is null || facts.Status == SessionStatus.Ended || displayName is null)
        {
            viewer.Send(ErrorFrame(ErrorCodes.SessionEnded, "session ended"));
            return false;
        }

        var live = _sessions.GetOrAdd(grant.SessionId, _ => new LiveSession(facts.Id, facts.Kind, facts.OwnerUserId, facts.Status, _timeProvider.GetUtcNow()));
        await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (live.IsEnded)
            {
                viewer.Send(ErrorFrame(ErrorCodes.SessionEnded, "session ended"));
                return false;
            }

            if (live.Viewers.Count >= MaxViewersPerSession)
            {
                viewer.Send(ErrorFrame(ErrorCodes.RateLimited, "too many viewers"));
                return false;
            }

            var peer = new ViewerPeer(viewer, grant.UserId, displayName);
            live.Viewers[viewer.Id] = peer;
            _sessionByPeer[viewer.Id] = live;

            viewer.Send(new Frame { AuthResult = new AuthResult { Ok = true, PeerId = viewer.Id } });
            viewer.Send(StateFrame(live));

            await AuditAsync(live, AuditEventTypes.SessionViewerJoined, AuditActorType.User, grant.UserId.ToString(), viewer, cancellationToken).ConfigureAwait(false);

            if (live.Agent is { } agent && live.Status != SessionStatus.Suspended)
            {
                agent.Send(new Frame { ViewerJoined = new ViewerJoined { SessionId = live.WireId, ViewerId = viewer.Id, DisplayName = displayName } });
                await RequestConsentLockedAsync(live, peer, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
        finally
        {
            live.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ frames

    /// <summary>Handles one frame from an attached peer.</summary>
    public async Task HandleFrameAsync(PeerConnection from, Frame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(frame);

        if (!_sessionByPeer.TryGetValue(from.Id, out var live))
        {
            return;
        }

        switch (frame.BodyCase)
        {
            case Frame.BodyOneofCase.SessionData:
                RouteSessionData(live, from, frame.SessionData);
                break;
            case Frame.BodyOneofCase.KeyExchange:
                RouteKeyExchange(live, from, frame.KeyExchange);
                break;
            case Frame.BodyOneofCase.Ping:
                from.Send(new Frame { Pong = new Pong { TimestampMs = frame.Ping.TimestampMs } });
                break;
            case Frame.BodyOneofCase.Pong:
                break; // any message refreshes the heartbeat
            case Frame.BodyOneofCase.ConsentResponse when from.Role == PeerRole.Agent:
                await HandleConsentResponseAsync(live, frame.ConsentResponse, cancellationToken).ConfigureAwait(false);
                break;
            case Frame.BodyOneofCase.EndSession:
                await EndAsync(
                    live,
                    from.Role == PeerRole.Agent ? SessionEndReasons.EndedByCustomer : SessionEndReasons.EndedBySupporter,
                    from.Role == PeerRole.Agent ? AuditActorType.Guest : AuditActorType.User,
                    from.Role == PeerRole.Agent ? null : ViewerUserId(live, from),
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                LogIgnoredFrame(_logger, frame.BodyCase, from.Role);
                break;
        }
    }

    /// <summary>Called when a peer's connection is gone (closed, aborted or failed).</summary>
    public async Task DetachAsync(PeerConnection peer)
    {
        ArgumentNullException.ThrowIfNull(peer);

        if (!_sessionByPeer.TryRemove(peer.Id, out var live))
        {
            return;
        }

        await live.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (live.IsEnded)
            {
                return;
            }

            if (ReferenceEquals(live.Agent, peer))
            {
                live.Agent = null;
                foreach (var viewer in live.Viewers.Values.Where(v => v.Consent == ViewerConsent.Requested))
                {
                    viewer.Consent = ViewerConsent.NotAsked; // asked again after a resume
                }

                await SetStatusLockedAsync(live, SessionStatus.Suspended, CancellationToken.None).ConfigureAwait(false);
                await AuditAsync(live, AuditEventTypes.SessionSuspended, AuditActorType.System, null, peer, CancellationToken.None).ConfigureAwait(false);
            }
            else if (live.Viewers.Remove(peer.Id, out var viewer))
            {
                live.RefreshGrantedViewers();
                live.Agent?.Send(new Frame { ViewerLeft = new ViewerLeft { SessionId = live.WireId, ViewerId = peer.Id } });
                await AuditAsync(live, AuditEventTypes.SessionViewerLeft, AuditActorType.User, viewer.UserId.ToString(), peer, CancellationToken.None).ConfigureAwait(false);

                if (live.GrantedViewers.IsEmpty && live.Status is SessionStatus.Active or SessionStatus.AwaitingConsent
                    && !live.Viewers.Values.Any(v => v.Consent == ViewerConsent.Requested))
                {
                    await SetStatusLockedAsync(live, SessionStatus.WaitingForViewer, CancellationToken.None).ConfigureAwait(false);
                }
            }

            if (live.Agent is null && live.Viewers.Count == 0 && live.Status == SessionStatus.WaitingForAgent)
            {
                _sessions.TryRemove(live.Id, out _); // nothing live left; the code stays valid
            }
        }
        finally
        {
            live.Gate.Release();
        }
    }

    // ------------------------------------------------------------------ maintenance

    /// <summary>Heartbeats and timeouts. Called every few seconds by <see cref="RelayMaintenance"/>.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var settings = _options.CurrentValue;

        foreach (var live in _sessions.Values)
        {
            // Read the session under its gate (viewers are a plain dictionary), act afterwards.
            List<PeerConnection> peers;
            List<string> timedOut;
            string? endReason = null;
            await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (live.IsEnded)
                {
                    continue;
                }

                peers = live.Peers().ToList();
                timedOut = live.Viewers.Values
                    .Where(v => v.Consent == ViewerConsent.Requested && now - v.ConsentRequestedAt >= settings.ConsentTimeout)
                    .Select(v => v.Connection.Id)
                    .ToList();

                if (live.Status == SessionStatus.Suspended && now - live.StatusSince >= settings.SuspendGracePeriod)
                {
                    endReason = SessionEndReasons.ConnectionLost;
                }
                else if (live.Status == SessionStatus.WaitingForViewer && live.Agent is not null && now - live.StatusSince >= settings.ViewerWaitTimeout)
                {
                    endReason = SessionEndReasons.NoViewer;
                }
            }
            finally
            {
                live.Gate.Release();
            }

            foreach (var peer in peers)
            {
                if (now - peer.LastReceived > HeartbeatTimeout)
                {
                    LogHeartbeatTimeout(_logger, peer.Role, live.Id);
                    peer.Abort();
                }
                else if (now - peer.LastPingSent >= HeartbeatInterval)
                {
                    peer.LastPingSent = now;
                    peer.Send(new Frame { Ping = new Ping { TimestampMs = (ulong)now.ToUnixTimeMilliseconds() } });
                }
            }

            if (endReason is not null)
            {
                await EndAsync(live, endReason, AuditActorType.System, null, cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (var viewerId in timedOut)
            {
                await RejectViewerAsync(live, viewerId, SessionEndReasons.ConsentTimeout, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _sessionService.SessionEnded -= OnSessionEndedInDatabase;

    // ------------------------------------------------------------------ internals

    private async Task ConnectAgentLockedAsync(LiveSession live, PeerConnection agent, bool resumed, CancellationToken cancellationToken)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        live.ResumeTokenHash = HashToken(token);
        live.Agent = agent;
        _sessionByPeer[agent.Id] = live;

        agent.Send(new Frame { AuthResult = new AuthResult { Ok = true, PeerId = agent.Id, ResumeToken = token } });
        agent.Send(new Frame
        {
            SessionAssigned = new SessionAssigned
            {
                SessionId = live.WireId,
                ConsentPolicy = ConsentPolicy.Ask, // ad-hoc: always ask (ADR-0014); permanent policies follow in Phase 10
                Recording = false,
                OperatorName = live.OperatorName ?? string.Empty,
            },
        });

        if (resumed)
        {
            await AuditAsync(live, AuditEventTypes.SessionResumed, AuditActorType.Guest, null, agent, cancellationToken).ConfigureAwait(false);
        }

        // Tell the agent who is already there; granted viewers continue, all others are asked.
        foreach (var viewer in live.Viewers.Values)
        {
            agent.Send(new Frame { ViewerJoined = new ViewerJoined { SessionId = live.WireId, ViewerId = viewer.Connection.Id, DisplayName = viewer.DisplayName } });
        }

        var target = live.GrantedViewers.IsEmpty ? SessionStatus.WaitingForViewer : SessionStatus.Active;
        await SetStatusLockedAsync(live, target, cancellationToken).ConfigureAwait(false);

        foreach (var viewer in live.Viewers.Values.Where(v => v.Consent == ViewerConsent.NotAsked).ToList())
        {
            await RequestConsentLockedAsync(live, viewer, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequestConsentLockedAsync(LiveSession live, ViewerPeer viewer, CancellationToken cancellationToken)
    {
        viewer.Consent = ViewerConsent.Requested;
        viewer.ConsentRequestedAt = _timeProvider.GetUtcNow();

        if (live.Status != SessionStatus.Active)
        {
            await SetStatusLockedAsync(live, SessionStatus.AwaitingConsent, cancellationToken).ConfigureAwait(false);
        }

        live.Agent?.Send(new Frame
        {
            ConsentRequest = new ConsentRequest
            {
                SessionId = live.WireId,
                ViewerId = viewer.Connection.Id,
                SupporterDisplayName = viewer.DisplayName,
                RequestedPermissions = { RequestedPermissions },
            },
        });
    }

    private async Task HandleConsentResponseAsync(LiveSession live, ConsentResponse response, CancellationToken cancellationToken)
    {
        if (!response.Granted)
        {
            await RejectViewerAsync(live, response.ViewerId, SessionEndReasons.ConsentDenied, cancellationToken).ConfigureAwait(false);
            return;
        }

        await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (live.IsEnded || !live.Viewers.TryGetValue(response.ViewerId ?? string.Empty, out var viewer) || viewer.Consent != ViewerConsent.Requested)
            {
                return;
            }

            viewer.Consent = ViewerConsent.Granted;
            live.RefreshGrantedViewers();
            await AuditConsentAsync(live, viewer, "granted", cancellationToken).ConfigureAwait(false);

            if (live.Status != SessionStatus.Active)
            {
                await SetStatusLockedAsync(live, SessionStatus.Active, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                viewer.Connection.Send(StateFrame(live));
            }

            if (!live.WasStarted)
            {
                live.WasStarted = true;
                await _auditLog.WriteAsync(new AuditEntry(
                    AuditEventTypes.SessionStarted, AuditActorType.System, TargetType: "session", TargetId: live.WireId,
                    Details: new Dictionary<string, string?> { ["recording"] = "false" }), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <summary>Removes a viewer whose consent was denied or timed out; ends the session if nobody else is there.</summary>
    private async Task RejectViewerAsync(LiveSession live, string? viewerId, string reason, CancellationToken cancellationToken)
    {
        var endSession = false;
        await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (live.IsEnded || !live.Viewers.TryGetValue(viewerId ?? string.Empty, out var viewer) || viewer.Consent != ViewerConsent.Requested)
            {
                return;
            }

            live.Viewers.Remove(viewer.Connection.Id);
            _sessionByPeer.TryRemove(viewer.Connection.Id, out _);
            live.RefreshGrantedViewers();
            await AuditConsentAsync(live, viewer, reason == SessionEndReasons.ConsentTimeout ? "timeout" : "denied", cancellationToken).ConfigureAwait(false);

            viewer.Connection.Send(ErrorFrame(ErrorCodes.ConsentDenied, reason));
            viewer.Connection.Complete();
            live.Agent?.Send(new Frame { ViewerLeft = new ViewerLeft { SessionId = live.WireId, ViewerId = viewer.Connection.Id } });

            if (live.Viewers.Count == 0)
            {
                endSession = true;
            }
            else if (live.GrantedViewers.IsEmpty && !live.Viewers.Values.Any(v => v.Consent == ViewerConsent.Requested))
            {
                await SetStatusLockedAsync(live, SessionStatus.WaitingForViewer, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            live.Gate.Release();
        }

        if (endSession)
        {
            await EndAsync(live, reason, AuditActorType.Guest, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RouteSessionData(LiveSession live, PeerConnection from, SessionData data)
    {
        if (data.SessionId != live.WireId)
        {
            return;
        }

        var droppable = data.Channel == (uint)Channel.Video;
        if (from.Role == PeerRole.Viewer)
        {
            // Viewers may only talk once the customer agreed; input only from the controlling viewer.
            if (!live.GrantedViewers.ContainsKey(from.Id) || (data.Channel == (uint)Channel.Input && live.ControllerViewerId != from.Id))
            {
                return;
            }

            data.ViewerId = from.Id; // never trust the sender's claim
            if (live.Agent is { } agent && agent.Send(new Frame { SessionData = data }, droppable))
            {
                live.AddRelayedBytes(data.Ciphertext.Length);
            }

            return;
        }

        var granted = live.GrantedViewers;
        if (string.IsNullOrEmpty(data.ViewerId))
        {
            var frame = new Frame { SessionData = data };
            foreach (var viewer in granted.Values)
            {
                if (viewer.Connection.Send(frame, droppable))
                {
                    live.AddRelayedBytes(data.Ciphertext.Length);
                }
            }
        }
        else if (granted.TryGetValue(data.ViewerId, out var target) && target.Connection.Send(new Frame { SessionData = data }, droppable))
        {
            live.AddRelayedBytes(data.Ciphertext.Length);
        }
    }

    private static void RouteKeyExchange(LiveSession live, PeerConnection from, KeyExchange exchange)
    {
        if (exchange.EphemeralPublicKey.Length is 0 or > 512)
        {
            return;
        }

        exchange.SessionId = live.WireId;
        if (from.Role == PeerRole.Viewer)
        {
            if (live.GrantedViewers.ContainsKey(from.Id))
            {
                exchange.ViewerId = from.Id;
                exchange.DeviceSignature = ByteString.Empty; // only agents sign
                live.Agent?.Send(new Frame { KeyExchange = exchange });
            }

            return;
        }

        if (live.GrantedViewers.TryGetValue(exchange.ViewerId, out var target))
        {
            target.Connection.Send(new Frame { KeyExchange = exchange });
        }
    }

    private async Task EndAsync(LiveSession live, string reason, AuditActorType actorType, string? actorId, CancellationToken cancellationToken)
    {
        await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (live.IsEnded)
            {
                return;
            }

            // Persist first (raises SessionEnded; its handler waits for the gate and then finds the session closed),
            // then tell the peers.
            live.Status = SessionStatus.Ended;
            await _sessionService.EndBySystemAsync(live.Id, reason, live.BytesRelayed, actorType, actorId, cancellationToken).ConfigureAwait(false);
            CloseLocked(live, reason);
        }
        finally
        {
            live.Gate.Release();
        }
    }

    private void CloseLocked(LiveSession live, string reason)
    {
        live.Status = SessionStatus.Ended;
        var ended = new Frame { SessionState = new SessionState { SessionId = live.WireId, Status = WireStatus.Ended, EndReason = reason } };
        foreach (var peer in live.Peers().ToList())
        {
            peer.Send(ended);
            peer.Complete();
            _sessionByPeer.TryRemove(peer.Id, out _);
        }

        live.Agent = null;
        live.Viewers.Clear();
        live.RefreshGrantedViewers();
        _sessions.TryRemove(live.Id, out _);
    }

    /// <summary>A supporter, an Admin or the maintenance job ended the session in the database: close the connections.</summary>
    private void OnSessionEndedInDatabase(object? sender, SessionEndedEventArgs e)
    {
        if (_sessions.TryGetValue(e.SessionId, out var live))
        {
            _ = CloseFromDatabaseAsync(live, e.Reason);
        }
    }

    private async Task CloseFromDatabaseAsync(LiveSession live, string reason)
    {
        try
        {
            await live.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!live.IsEnded)
                {
                    CloseLocked(live, reason);
                }
            }
            finally
            {
                live.Gate.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
    }

    private async Task SetStatusLockedAsync(LiveSession live, SessionStatus status, CancellationToken cancellationToken)
    {
        if (live.Status == status)
        {
            return;
        }

        live.Status = status;
        live.StatusSince = _timeProvider.GetUtcNow();

        // Persist first, then notify: whoever reacts to the notification sees the stored state.
        await _sessionService.UpdateStatusAsync(live.Id, status, cancellationToken).ConfigureAwait(false);

        var frame = StateFrame(live);
        foreach (var peer in live.Peers())
        {
            peer.Send(frame);
        }
    }

    private Task AuditAsync(LiveSession live, string eventType, AuditActorType actorType, string? actorId, PeerConnection peer, CancellationToken cancellationToken) =>
        _auditLog.WriteAsync(new AuditEntry(
            eventType, actorType, ActorId: actorId, Ip: peer.RemoteAddress?.ToString(),
            TargetType: "session", TargetId: live.WireId), cancellationToken);

    private Task AuditConsentAsync(LiveSession live, ViewerPeer viewer, string result, CancellationToken cancellationToken) =>
        _auditLog.WriteAsync(new AuditEntry(
            AuditEventTypes.SessionConsent, AuditActorType.Guest, Ip: live.Agent?.RemoteAddress?.ToString(),
            TargetType: "session", TargetId: live.WireId,
            Details: new Dictionary<string, string?> { ["result"] = result, ["supporter"] = viewer.UserId.ToString() }), cancellationToken);

    /// <summary>Uses the immutable snapshot – safe without holding the gate.</summary>
    private static string? ViewerUserId(LiveSession live, PeerConnection peer) =>
        live.GrantedViewers.TryGetValue(peer.Id, out var viewer) ? viewer.UserId.ToString() : null;

    private static Frame StateFrame(LiveSession live) =>
        new() { SessionState = new SessionState { SessionId = live.WireId, Status = (WireStatus)(int)live.Status } };

    internal static Frame ErrorFrame(string code, string detail) => new() { Error = new Error { Code = code, Detail = detail } };

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored {Frame} frame from {Role}")]
    private static partial void LogIgnoredFrame(ILogger logger, Frame.BodyOneofCase frame, PeerRole role);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Role} of session {SessionId} timed out (no heartbeat)")]
    private static partial void LogHeartbeatTimeout(ILogger logger, PeerRole role, Guid sessionId);
}
