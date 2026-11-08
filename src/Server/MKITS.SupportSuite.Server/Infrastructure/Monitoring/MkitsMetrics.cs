// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Diagnostics.Metrics;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Server.Infrastructure.Monitoring;

/// <summary>
/// The server's own metrics (14.3, meter <see cref="MeterName"/>): relay traffic and connections, live sessions and
/// devices, failed attempts. Only counts – never session ids, codes, names or addresses as tags.
/// </summary>
public sealed class MkitsMetrics
{
    public const string MeterName = "MKITS.SupportSuite.Server";

    private readonly Counter<long> _relayBytes;
    private readonly Counter<long> _droppedFrames;
    private readonly UpDownCounter<long> _connections;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _bans;

    public MkitsMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _relayBytes = meter.CreateCounter<long>("mkits.relay.bytes", "By", "Relay traffic by direction (in: from peers, out: to peers) and peer role.");
        _droppedFrames = meter.CreateCounter<long>("mkits.relay.dropped_frames", "{frame}", "Droppable frames (video) not sent because the peer was too slow.");
        _connections = meter.CreateUpDownCounter<long>("mkits.relay.connections", "{connection}", "Open relay WebSocket connections by peer role.");
        _failures = meter.CreateCounter<long>("mkits.auth.failures", "{failure}", "Failed attempts counted towards IP bans, by kind.");
        _bans = meter.CreateCounter<long>("mkits.ip_bans", "{ban}", "Automatic IP bans.");
        Meter = meter;
    }

    internal Meter Meter { get; }

    /// <summary>Gauges read from the session hub on every scrape.</summary>
    public void ObserveHub(Func<int> liveSessions, Func<int> onlineDevices)
    {
        Meter.CreateObservableGauge("mkits.sessions.live", liveSessions, "{session}", "Sessions currently held by the relay.");
        Meter.CreateObservableGauge("mkits.devices.online", onlineDevices, "{device}", "Installed agents currently connected.");
    }

    public void Received(PeerRole role, long bytes) => _relayBytes.Add(bytes, new("direction", "in"), new("role", Role(role)));

    public void Sent(PeerRole role, long bytes) => _relayBytes.Add(bytes, new("direction", "out"), new("role", Role(role)));

    public void FrameDropped(PeerRole role) => _droppedFrames.Add(1, new KeyValuePair<string, object?>("role", Role(role)));

    public void ConnectionOpened(PeerRole role) => _connections.Add(1, new KeyValuePair<string, object?>("role", Role(role)));

    public void ConnectionClosed(PeerRole role) => _connections.Add(-1, new KeyValuePair<string, object?>("role", Role(role)));

    /// <param name="kind">One of the fixed failure kinds of the IP ban service (never user input).</param>
    public void Failure(string kind) => _failures.Add(1, new KeyValuePair<string, object?>("kind", kind));

    public void Banned() => _bans.Add(1);

    private static string Role(PeerRole role) => role switch
    {
        PeerRole.Agent => "agent",
        PeerRole.Viewer => "viewer",
        _ => "other",
    };
}
