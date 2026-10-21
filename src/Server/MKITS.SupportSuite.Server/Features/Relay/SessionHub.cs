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
using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.Sessions;
using SessionOptions = MKITS.SupportSuite.Server.Features.Sessions.SessionOptions;
using SessionStatus = MKITS.SupportSuite.Server.Features.Sessions.SessionStatus;
using WireStatus = MKITS.SupportSuite.Common.Protocol.V0.SessionStatus;

namespace MKITS.SupportSuite.Server.Features.Relay;

/// <summary>An online permanent device: its connection stays open between sessions (idle presence).</summary>
public sealed class DeviceConnection(DeviceFacts facts, PeerConnection connection)
{
    public DeviceFacts Facts { get; } = facts;

    public PeerConnection Connection { get; } = connection;
}

/// <summary>
/// The session broker (docs/session-lifecycle.md §3, docs/protocol.md §4–5): attaches authenticated peers to
/// sessions, drives the state machine (consent, suspend/resume, timeouts), and routes session-plane frames.
/// It also keeps the presence of permanent devices, whose connection outlives their sessions.
/// The server never decrypts <c>SessionData</c>; it only reads the routing header – including the channel, which
/// is enough to enforce the viewers' permissions (the agent rejects payloads that do not match their channel).
/// </summary>
public sealed partial class SessionHub : IDisposable
{
    /// <summary>Upper limit of supporters watching one session.</summary>
    internal const int MaxViewersPerSession = 5;

    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();
    private readonly ConcurrentDictionary<string, LiveSession> _sessionByPeer = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, DeviceConnection> _devices = new();
    private readonly ConcurrentDictionary<string, Guid> _deviceByPeer = new(StringComparer.Ordinal);

    /// <summary>Serializes starting device sessions, so one device never gets two sessions at once.</summary>
    private readonly SemaphoreSlim _deviceSessionGate = new(1, 1);
    private readonly SessionService _sessionService;
    private readonly DeviceService _deviceService;
    private readonly IOptionsMonitor<SessionOptions> _options;
    private readonly IAuditLog _auditLog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionHub> _logger;

    public SessionHub(
        SessionService sessionService, DeviceService deviceService, IOptionsMonitor<SessionOptions> options, IAuditLog auditLog,
        TimeProvider timeProvider, ILogger<SessionHub> logger)
    {
        _sessionService = sessionService;
        _deviceService = deviceService;
        _options = options;
        _auditLog = auditLog;
        _timeProvider = timeProvider;
        _logger = logger;
        _sessionService.SessionEnded += OnSessionEndedInDatabase;
        _deviceService.DeviceChanged += OnDeviceChanged;
    }

    /// <summary>Number of sessions with at least one connected peer.</summary>
    public int LiveSessionCount => _sessions.Count;

    /// <summary>Live status of a session as seen by the relay (for tests and the supporter UI).</summary>
    public SessionStatus? GetLiveStatus(Guid sessionId) => _sessions.TryGetValue(sessionId, out var live) ? live.Status : null;

    /// <summary>Whether the permanent device has a connection right now.</summary>
    public bool IsDeviceOnline(Guid deviceId) => _devices.ContainsKey(deviceId);

    /// <summary>The session the device is in (also while it is reconnecting), or <c>null</c> when it is idle.</summary>
    public Guid? GetDeviceSessionId(Guid deviceId) => FindDeviceSession(deviceId)?.Id;

    // ------------------------------------------------------------------ permanent devices

    /// <summary>
    /// Registers an authenticated permanent device (idle presence) and sends <c>AuthResult</c>. A previous connection
    /// of the same device is dropped. If the device was in a session that is still waiting for it (it lost its
    /// connection), it is bound to that session again – the signature proves it is the same device.
    /// </summary>
    public async Task<bool> AttachDeviceAsync(PeerConnection agent, DeviceFacts facts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(facts);

        DeviceConnection? previous;
        await _deviceSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _deviceByPeer[agent.Id] = facts.Id;
            previous = _devices.TryGetValue(facts.Id, out var old) ? old : null;
            _devices[facts.Id] = new DeviceConnection(facts, agent);
            agent.Send(new Frame { AuthResult = new AuthResult { Ok = true, PeerId = agent.Id } });
            LogDeviceOnline(_logger, facts.Id);

            if (FindDeviceSession(facts.Id) is { } live)
            {
                await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (!live.IsEnded)
                    {
                        if (live.Agent is { } stale)
                        {
                            _sessionByPeer.TryRemove(stale.Id, out _);
                            live.Agent = null;
                        }

                        await ConnectAgentLockedAsync(live, agent, resumed: true, sendAuthResult: false, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    live.Gate.Release();
                }
            }
        }
        finally
        {
            _deviceSessionGate.Release();
        }

        if (previous is not null)
        {
            _deviceByPeer.TryRemove(previous.Connection.Id, out _);
            previous.Connection.Abort();
        }

        return true;
    }

    /// <summary>
    /// Binds the online, idle device to a new permanent session created by <see cref="DeviceSessionLauncher"/>.
    /// Returns <c>false</c> if the device is offline or already in a session.
    /// </summary>
    public async Task<bool> StartDeviceSessionAsync(Guid sessionId, Guid deviceId, string operatorName, CancellationToken cancellationToken)
    {
        var facts = await _sessionService.GetFactsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (facts is null || facts.Status == SessionStatus.Ended)
        {
            return false;
        }

        await _deviceSessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_devices.TryGetValue(deviceId, out var device) || FindDeviceSession(deviceId) is not null)
            {
                return false;
            }

            var live = new LiveSession(facts.Id, SessionKind.Permanent, facts.OwnerUserId, facts.Status, _timeProvider.GetUtcNow())
            {
                DeviceId = deviceId,
                ConsentPolicy = device.Facts.ConsentPolicy,
                OperatorName = operatorName,
            };

            await live.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _sessions[live.Id] = live;
                await ConnectAgentLockedAsync(live, device.Connection, resumed: false, sendAuthResult: false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                live.Gate.Release();
            }

            return true;
        }
        finally
        {
            _deviceSessionGate.Release();
        }
    }

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
            await ConnectAgentLockedAsync(live, agent, resumed: false, sendAuthResult: true, cancellationToken).ConfigureAwait(false);
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

            await ConnectAgentLockedAsync(live, agent, resumed: true, sendAuthResult: true, cancellationToken).ConfigureAwait(false);
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

        // Permanent sessions exist live only while the device is bound to them (created by StartDeviceSessionAsync).
        LiveSession? live;
        if (facts.Kind == SessionKind.Permanent)
        {
            live = _sessions.TryGetValue(grant.SessionId, out var existing) ? existing : null;
        }
        else
        {
            live = _sessions.GetOrAdd(grant.SessionId, _ => new LiveSession(facts.Id, facts.Kind, facts.OwnerUserId, facts.Status, _timeProvider.GetUtcNow()));
        }

        if (live is null)
        {
            viewer.Send(ErrorFrame(ErrorCodes.SessionEnded, "session ended"));
            return false;
        }

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

            var peer = new ViewerPeer(viewer, grant.UserId, displayName, grant.Permissions);
            live.Viewers[viewer.Id] = peer;
            _sessionByPeer[viewer.Id] = live;

            viewer.Send(new Frame { AuthResult = new AuthResult { Ok = true, PeerId = viewer.Id } });
            viewer.Send(StateFrame(live));

            await AuditAsync(live, AuditEventTypes.SessionViewerJoined, AuditActorType.User, grant.UserId.ToString(), viewer, cancellationToken).ConfigureAwait(false);

            if (live.Agent is { } agent && live.Status != SessionStatus.Suspended)
            {
                agent.Send(ViewerJoinedFrame(live, peer));
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
            // Idle device: only the heartbeat matters (any frame refreshes it).
            if (frame.BodyCase == Frame.BodyOneofCase.Ping && _deviceByPeer.ContainsKey(from.Id))
            {
                from.Send(new Frame { Pong = new Pong { TimestampMs = frame.Ping.TimestampMs } });
            }

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

        if (_deviceByPeer.TryRemove(peer.Id, out var deviceId)
            && _devices.TryGetValue(deviceId, out var device) && ReferenceEquals(device.Connection, peer))
        {
            _devices.TryRemove(new KeyValuePair<Guid, DeviceConnection>(deviceId, device));
            LogDeviceOffline(_logger, deviceId);
        }

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
                else if (live.Status == SessionStatus.WaitingForViewer && live.Agent is not null
                    && now - live.StatusSince >= (live.Kind == SessionKind.Permanent ? settings.DeviceSessionIdleTimeout : settings.ViewerWaitTimeout))
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
                Heartbeat(peer, now, live.Id);
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

    /// <summary>Heartbeat for devices that are online but in no session (those in a session are handled above).</summary>
    public void TickIdleDevices()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var device in _devices.Values)
        {
            if (!_sessionByPeer.ContainsKey(device.Connection.Id))
            {
                Heartbeat(device.Connection, now, null);
            }
        }
    }

    public void Dispose()
    {
        _sessionService.SessionEnded -= OnSessionEndedInDatabase;
        _deviceService.DeviceChanged -= OnDeviceChanged;
        _deviceSessionGate.Dispose();
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// Binds the agent to the session. <c>sendAuthResult</c> is <c>true</c> for ad-hoc agents (AuthResult with a
    /// fresh resume token); permanent devices got their AuthResult when they came online and re-authenticate with
    /// their key after a connection loss.
    /// </summary>
    private async Task ConnectAgentLockedAsync(LiveSession live, PeerConnection agent, bool resumed, bool sendAuthResult, CancellationToken cancellationToken)
    {
        live.Agent = agent;
        _sessionByPeer[agent.Id] = live;

        if (sendAuthResult)
        {
            var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            live.ResumeTokenHash = HashToken(token);
            agent.Send(new Frame { AuthResult = new AuthResult { Ok = true, PeerId = agent.Id, ResumeToken = token } });
        }

        agent.Send(new Frame
        {
            SessionAssigned = new SessionAssigned
            {
                SessionId = live.WireId,
                ConsentPolicy = (ConsentPolicy)(int)live.ConsentPolicy, // ad-hoc: always Ask; permanent: the device group policy (ADR-0014). Enum values match the wire enum.
                Recording = false,
                OperatorName = live.OperatorName ?? string.Empty,
            },
        });

        if (!resumed && live.Kind == SessionKind.Permanent)
        {
            await AuditAsync(live, AuditEventTypes.SessionAgentConnected, AuditActorType.Device, live.DeviceId?.ToString(), agent, cancellationToken).ConfigureAwait(false);
        }

        if (resumed)
        {
            await AuditAsync(live, AuditEventTypes.SessionResumed, AuditActorType.Guest, null, agent, cancellationToken).ConfigureAwait(false);
        }

        // Tell the agent who is already there; granted viewers continue, all others are asked.
        foreach (var viewer in live.Viewers.Values)
        {
            agent.Send(ViewerJoinedFrame(live, viewer));
        }

        var target = live.GrantedViewers.IsEmpty ? SessionStatus.WaitingForViewer : SessionStatus.Active;
        if (resumed && target == live.Status)
        {
            // The agent lost its keys with the old connection: viewers must see "Active" again to re-key.
            var state = StateFrame(live);
            foreach (var viewer in live.GrantedViewers.Values)
            {
                viewer.Connection.Send(state);
            }
        }

        await SetStatusLockedAsync(live, target, cancellationToken).ConfigureAwait(false);

        foreach (var viewer in live.Viewers.Values.Where(v => v.Consent == ViewerConsent.NotAsked).ToList())
        {
            await RequestConsentLockedAsync(live, viewer, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequestConsentLockedAsync(LiveSession live, ViewerPeer viewer, CancellationToken cancellationToken)
    {
        if (live.ConsentPolicy != DeviceConsentPolicy.Ask)
        {
            // Notify / silent (permanent devices only): no question; the agent shows the banner for "notify".
            await GrantLockedAsync(live, viewer, live.ConsentPolicy == DeviceConsentPolicy.Notify ? "policy_notify" : "policy_silent", cancellationToken).ConfigureAwait(false);
            return;
        }

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
                RequestedPermissions = { DeviceService.PermissionNames(viewer.Permissions) },
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

            await GrantLockedAsync(live, viewer, "granted", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            live.Gate.Release();
        }
    }

    /// <summary>Lets the viewer onto the data plane (consent given or not required by the policy).</summary>
    private async Task GrantLockedAsync(LiveSession live, ViewerPeer viewer, string auditResult, CancellationToken cancellationToken)
    {
        viewer.Consent = ViewerConsent.Granted;
        live.RefreshGrantedViewers();
        await AuditConsentAsync(live, viewer, auditResult, cancellationToken).ConfigureAwait(false);

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
                Details: new Dictionary<string, string?> { ["recording"] = "false", ["consent_policy"] = live.ConsentPolicy.ToString() }), cancellationToken).ConfigureAwait(false);
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
        var required = RequiredPermission(data.Channel);
        if (from.Role == PeerRole.Viewer)
        {
            // Viewers may only talk once the customer agreed, only on channels they have the permission for,
            // and input only from the controlling viewer.
            if (!live.GrantedViewers.TryGetValue(from.Id, out var sender) || !sender.Permissions.HasFlag(required)
                || (data.Channel == (uint)Channel.Input && live.ControllerViewerId != from.Id))
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
            foreach (var viewer in granted.Values.Where(v => v.Permissions.HasFlag(required)))
            {
                if (viewer.Connection.Send(frame, droppable))
                {
                    live.AddRelayedBytes(data.Ciphertext.Length);
                }
            }
        }
        else if (granted.TryGetValue(data.ViewerId, out var target) && target.Permissions.HasFlag(required)
            && target.Connection.Send(new Frame { SessionData = data }, droppable))
        {
            live.AddRelayedBytes(data.Ciphertext.Length);
        }
    }

    /// <summary>
    /// Permission a viewer needs to send or receive on a channel. Unknown channels need all permissions, so a newer
    /// agent can never open a side door for restricted viewers.
    /// </summary>
    internal static DevicePermissions RequiredPermission(uint channel) => (Channel)channel switch
    {
        Channel.Video or Channel.Display or Channel.Chat => DevicePermissions.View,
        Channel.Input => DevicePermissions.Control,
        Channel.Clipboard => DevicePermissions.Clipboard,
        Channel.File => DevicePermissions.Files,
        Channel.Shell => DevicePermissions.Shell,
        _ => DevicePermissions.All,
    };

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
            _sessionByPeer.TryRemove(peer.Id, out _);
            if (!_deviceByPeer.ContainsKey(peer.Id))
            {
                peer.Complete(); // permanent devices stay connected (idle presence)
            }
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

    /// <summary>An Admin disabled, removed or moved a device: end its session and, unless it is still active, disconnect it.</summary>
    private void OnDeviceChanged(object? sender, DeviceChangedEventArgs e) => _ = ReevaluateDeviceAsync(e.DeviceId);

    private async Task ReevaluateDeviceAsync(Guid deviceId)
    {
        try
        {
            var facts = await _deviceService.GetFactsAsync(deviceId).ConfigureAwait(false);
            if (facts is { Status: DeviceStatus.Active })
            {
                // Moved to another group: new sessions use the new policy; permissions are checked on every join.
                if (_devices.TryGetValue(deviceId, out var current))
                {
                    _devices.TryUpdate(deviceId, new DeviceConnection(facts, current.Connection), current);
                }

                return;
            }

            if (FindDeviceSession(deviceId) is { } live)
            {
                await EndAsync(live, SessionEndReasons.DeviceDisabled, AuditActorType.System, null, CancellationToken.None).ConfigureAwait(false);
            }

            if (_devices.TryGetValue(deviceId, out var device))
            {
                device.Connection.Send(ErrorFrame(ErrorCodes.NotAuthorized, "device disabled or removed"));
                device.Connection.Complete();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or OperationCanceledException)
        {
            // shutting down
        }
    }

    private LiveSession? FindDeviceSession(Guid deviceId) =>
        _sessions.Values.FirstOrDefault(s => s.DeviceId == deviceId && !s.IsEnded);

    private void Heartbeat(PeerConnection peer, DateTimeOffset now, Guid? sessionId)
    {
        if (now - peer.LastReceived > HeartbeatTimeout)
        {
            LogHeartbeatTimeout(_logger, peer.Role, sessionId);
            peer.Abort();
        }
        else if (now - peer.LastPingSent >= HeartbeatInterval)
        {
            peer.LastPingSent = now;
            peer.Send(new Frame { Ping = new Ping { TimestampMs = (ulong)now.ToUnixTimeMilliseconds() } });
        }
    }

    private static Frame ViewerJoinedFrame(LiveSession live, ViewerPeer viewer) => new()
    {
        ViewerJoined = new ViewerJoined
        {
            SessionId = live.WireId,
            ViewerId = viewer.Connection.Id,
            DisplayName = viewer.DisplayName,
            Permissions = { DeviceService.PermissionNames(viewer.Permissions) },
        },
    };

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
    private static partial void LogHeartbeatTimeout(ILogger logger, PeerRole role, Guid? sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} is online")]
    private static partial void LogDeviceOnline(ILogger logger, Guid deviceId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Device {DeviceId} went offline")]
    private static partial void LogDeviceOffline(ILogger logger, Guid deviceId);
}
