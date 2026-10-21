// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Immutable;
using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.Sessions;

namespace MKITS.SupportSuite.Server.Features.Relay;

/// <summary>Consent state of one viewer.</summary>
public enum ViewerConsent
{
    /// <summary>The agent is not connected yet; the request is sent when it is.</summary>
    NotAsked,

    /// <summary><c>ConsentRequest</c> sent, waiting for the customer.</summary>
    Requested,

    /// <summary>The viewer may exchange keys and session data.</summary>
    Granted,
}

/// <summary>A connected supporter with what it may do in this session (from the viewer ticket).</summary>
public sealed class ViewerPeer(PeerConnection connection, Guid userId, string displayName, DevicePermissions permissions)
{
    public PeerConnection Connection { get; } = connection;

    public Guid UserId { get; } = userId;

    public string DisplayName { get; } = displayName;

    public DevicePermissions Permissions { get; } = permissions;

    public ViewerConsent Consent { get; set; } = ViewerConsent.NotAsked;

    public DateTimeOffset ConsentRequestedAt { get; set; }
}

/// <summary>
/// In-memory state of a session with at least one connected peer. All changes happen while holding
/// <see cref="Gate"/>; the data plane only reads the immutable snapshots <see cref="Agent"/> and
/// <see cref="GrantedViewers"/> so that video never waits for a state change.
/// </summary>
public sealed class LiveSession(Guid id, SessionKind kind, Guid ownerUserId, SessionStatus status, DateTimeOffset now)
{
    private long _bytesRelayed;
    private volatile PeerConnection? _agent;
    private volatile ImmutableDictionary<string, ViewerPeer> _grantedViewers = ImmutableDictionary<string, ViewerPeer>.Empty;

    public Guid Id { get; } = id;

    public string WireId { get; } = id.ToString();

    public SessionKind Kind { get; } = kind;

    /// <summary>The permanent device of this session (<c>null</c> for ad-hoc sessions).</summary>
    public Guid? DeviceId { get; init; }

    /// <summary>Ad-hoc sessions always ask (ADR-0014); permanent ones follow the device group.</summary>
    public DeviceConsentPolicy ConsentPolicy { get; init; } = DeviceConsentPolicy.Ask;

    public Guid OwnerUserId { get; } = ownerUserId;

    public SemaphoreSlim Gate { get; } = new(1, 1);

    public SessionStatus Status { get; set; } = status;

    /// <summary>When <see cref="Status"/> last changed (drives the timeouts).</summary>
    public DateTimeOffset StatusSince { get; set; } = now;

    public bool IsEnded => Status == SessionStatus.Ended;

    public PeerConnection? Agent
    {
        get => _agent;
        set => _agent = value;
    }

    public string? OperatorName { get; set; }

    /// <summary>SHA-256 of the agent's current resume token.</summary>
    public byte[]? ResumeTokenHash { get; set; }

    /// <summary>All viewers (any consent state), keyed by viewer id. Only touched under <see cref="Gate"/>.</summary>
    public Dictionary<string, ViewerPeer> Viewers { get; } = new(StringComparer.Ordinal);

    /// <summary>Snapshot of viewers allowed on the data plane.</summary>
    public ImmutableDictionary<string, ViewerPeer> GrantedViewers => _grantedViewers;

    /// <summary>Viewer allowed to send input (first granted viewer with the Control permission; explicit hand-over follows with the viewer UI).</summary>
    public string? ControllerViewerId { get; set; }

    /// <summary><c>true</c> once the session was Active at least once (audit <c>session.started</c> only once).</summary>
    public bool WasStarted { get; set; }

    public long BytesRelayed => Interlocked.Read(ref _bytesRelayed);

    public void AddRelayedBytes(int count) => Interlocked.Add(ref _bytesRelayed, count);

    /// <summary>Rebuilds <see cref="GrantedViewers"/> after a change of <see cref="Viewers"/>. Call under <see cref="Gate"/>.</summary>
    public void RefreshGrantedViewers()
    {
        _grantedViewers = Viewers.Where(v => v.Value.Consent == ViewerConsent.Granted).ToImmutableDictionary(StringComparer.Ordinal);
        if (ControllerViewerId is not null && !_grantedViewers.ContainsKey(ControllerViewerId))
        {
            ControllerViewerId = null;
        }

        ControllerViewerId ??= _grantedViewers.Values
            .Where(v => v.Permissions.HasFlag(DevicePermissions.Control))
            .Select(v => v.Connection.Id)
            .FirstOrDefault();
    }

    /// <summary>All connected peers.</summary>
    public IEnumerable<PeerConnection> Peers()
    {
        if (_agent is { } agent)
        {
            yield return agent;
        }

        foreach (var viewer in Viewers.Values)
        {
            yield return viewer.Connection;
        }
    }
}
