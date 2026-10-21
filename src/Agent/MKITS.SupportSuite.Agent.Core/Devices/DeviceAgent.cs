// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Devices;

/// <summary>Why <see cref="DeviceAgent.RunAsync"/> returned (it normally runs until cancelled).</summary>
public enum DeviceAgentStopReason
{
    Cancelled,

    /// <summary>Not enrolled and no enrollment token configured.</summary>
    NotEnrolled,

    /// <summary>The enrollment token was rejected (invalid, expired, revoked or used up).</summary>
    EnrollmentRejected,
}

/// <summary>
/// The installed agent (docs/protocol.md §3, "Permanent devices"): enrolls once, then keeps a connection to the
/// server authenticated with the device key, waits idle, and serves the sessions supporters open over that
/// connection. Reconnects with backoff whenever the connection is lost; a session interrupted by a short loss
/// continues (the server binds the device again).
/// </summary>
public sealed class DeviceAgent : IAsyncDisposable
{
    private readonly DeviceAgentOptions _options;
    private readonly IWebSocketConnector _connector;
    private readonly IDeviceIdentityStore _store;
    private readonly IAgentUserInterface _ui;
    private readonly IReadOnlyList<ISessionChannelHandler> _handlers;
    private readonly TimeProvider _timeProvider;
    private SessionEngine? _engine;
    private DeviceIdentity? _identity;
    private volatile FrameSocket? _socket;

    public DeviceAgent(
        DeviceAgentOptions options,
        IWebSocketConnector connector,
        IDeviceIdentityStore store,
        IAgentUserInterface ui,
        IEnumerable<ISessionChannelHandler> handlers,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(handlers);

        _options = options;
        _connector = connector;
        _store = store;
        _ui = ui;
        _handlers = handlers.ToList();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The enrolled device id (after enrollment or loading the identity).</summary>
    public Guid? DeviceId => _identity?.DeviceId;

    /// <summary>The current session, if any.</summary>
    public string? SessionId => _engine?.SessionId;

    /// <summary>Runs until cancelled, or returns early when the device cannot be enrolled.</summary>
    public async Task<DeviceAgentStopReason> RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _identity ??= _store.Load();
            if (_identity is null)
            {
                if (string.IsNullOrWhiteSpace(_options.EnrollmentToken))
                {
                    return DeviceAgentStopReason.NotEnrolled;
                }

                _identity = await EnrollWithRetryAsync(cancellationToken).ConfigureAwait(false);
                if (_identity is null)
                {
                    return DeviceAgentStopReason.EnrollmentRejected;
                }
            }

            _engine = new SessionEngine(_ui, _handlers, _options.ServerUri.Host, _identity.Sign);
            var delay = _options.MinReconnectDelay;
            while (!cancellationToken.IsCancellationRequested)
            {
                _ui.OnStatusChanged(AgentStatus.Connecting, _options.ServerUri.Host);
                var outcome = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                switch (outcome)
                {
                    case null:
                        delay = _options.MinReconnectDelay;
                        await ServeAsync(cancellationToken).ConfigureAwait(false);

                        // Keys are bound to the connection. If the session survives (short loss), the server sends
                        // SessionAssigned again after the reconnect and announces the viewers again.
                        _engine.EndSession();
                        _ui.OnStatusChanged(AgentStatus.Reconnecting, null);
                        break;
                    case ErrorCodes.NotAuthorized:
                        _ui.OnStatusChanged(AgentStatus.PendingApproval, null);
                        await Task.Delay(_options.ApprovalPollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    case ErrorCodes.AuthFailed:
                        _ui.OnStatusChanged(AgentStatus.Ended, outcome);
                        await Task.Delay(_options.RejectedRetryInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    default:
                        _ui.OnStatusChanged(AgentStatus.Reconnecting, outcome);
                        break;
                }

                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // stopping
        }

        return DeviceAgentStopReason.Cancelled;
    }

    /// <summary>The end user ended the current session (the "End session" button is always available).</summary>
    public async Task EndSessionAsync()
    {
        if (_socket is { } socket && _engine?.SessionId is { } sessionId)
        {
            try
            {
                await socket.SendAsync(new Frame { EndSession = new EndSession { SessionId = sessionId, Reason = "ended_by_customer" } }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException)
            {
                // the session ends with the connection anyway
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket is { } socket)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }

        _engine?.ResetViewers();
        _identity?.Dispose();
    }

    // ------------------------------------------------------------------ enrollment

    /// <summary>Enrolls; retries connection problems, gives up when the token is rejected.</summary>
    private async Task<DeviceIdentity?> EnrollWithRetryAsync(CancellationToken cancellationToken)
    {
        var delay = _options.MinReconnectDelay;
        while (true)
        {
            _ui.OnStatusChanged(AgentStatus.Connecting, _options.ServerUri.Host);
            var key = DeviceIdentity.CreateKey();
            var (deviceId, error) = await EnrollAsync(key, cancellationToken).ConfigureAwait(false);
            if (deviceId is { } id)
            {
                var identity = DeviceIdentity.FromKey(id, key);
                _store.Save(identity);
                return identity;
            }

            key.Dispose();
            if (error is ErrorCodes.AuthFailed or ErrorCodes.IpBlocked)
            {
                _ui.OnStatusChanged(AgentStatus.Ended, error);
                return null;
            }

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
        }
    }

    private async Task<(Guid? DeviceId, string? Error)> EnrollAsync(System.Security.Cryptography.ECDsa key, CancellationToken cancellationToken)
    {
        var (socket, _, error) = await HelloAsync(cancellationToken).ConfigureAwait(false);
        if (socket is null)
        {
            return (null, error);
        }

        await using (socket.ConfigureAwait(false))
        {
            await socket.SendAsync(new Frame
            {
                Authenticate = new Authenticate
                {
                    Enrollment = new Enrollment
                    {
                        Token = _options.EnrollmentToken,
                        DevicePublicKey = ByteString.CopyFrom(key.ExportSubjectPublicKeyInfo()),
                        Machine = _options.Machine,
                    },
                },
            }, cancellationToken).ConfigureAwait(false);

            var result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            return result?.AuthResult is { Ok: true } auth && Guid.TryParse(auth.PeerId, out var deviceId)
                ? (deviceId, null)
                : (null, result?.Error?.Code ?? ErrorCodes.Internal);
        }
    }

    // ------------------------------------------------------------------ connection

    /// <summary>Connects and authenticates with the device key. Returns <c>null</c> on success, otherwise an error code.</summary>
    private async Task<string?> ConnectAsync(CancellationToken cancellationToken)
    {
        var (socket, ack, error) = await HelloAsync(cancellationToken).ConfigureAwait(false);
        if (socket is null)
        {
            return error;
        }

        await socket.SendAsync(new Frame
        {
            Authenticate = new Authenticate
            {
                DeviceSignature = new DeviceSignature
                {
                    DeviceId = _identity!.DeviceId.ToString(),
                    Signature = ByteString.CopyFrom(_identity.Sign(ack!.Nonce.ToByteArray())),
                },
            },
        }, cancellationToken).ConfigureAwait(false);

        var result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (result?.AuthResult is not { Ok: true })
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            return result?.Error?.Code ?? ErrorCodes.Internal;
        }

        _engine!.Attach(socket);
        var previous = Interlocked.Exchange(ref _socket, socket);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        _ui.OnStatusChanged(AgentStatus.Idle, null);
        return null;
    }

    private async Task<(FrameSocket? Socket, HelloAck? Ack, string? Error)> HelloAsync(CancellationToken cancellationToken)
    {
        FrameSocket socket;
        try
        {
            socket = new FrameSocket(await _connector.ConnectAsync(_options.AgentEndpoint, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or IOException)
        {
            return (null, null, ErrorCodes.Internal);
        }

        await socket.SendAsync(new Frame
        {
            Hello = new Hello
            {
                ProtocolMajor = ProtocolVersion.Major,
                ProtocolMinor = ProtocolVersion.Minor,
                Role = PeerRole.Agent,
                Platform = _options.Platform,
                ProductVersion = ProductInfo.Version,
                Capabilities = { _options.Capabilities },
            },
        }, cancellationToken).ConfigureAwait(false);

        var ack = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (ack?.BodyCase != Frame.BodyOneofCase.HelloAck)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            return (null, null, ack?.Error?.Code ?? ErrorCodes.ProtocolMismatch);
        }

        return (socket, ack.HelloAck, null);
    }

    /// <summary>Idle presence and sessions on one connection, until it is lost.</summary>
    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        var socket = _socket!;
        var engine = _engine!;
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return; // connection lost or closed by the server
            }

            string? ended = null;
            if (await engine.HandleAsync(frame, cancellationToken, reason => ended = reason).ConfigureAwait(false))
            {
                if (ended is not null)
                {
                    engine.EndSession();
                    _ui.OnStatusChanged(AgentStatus.Idle, ended);
                }

                continue;
            }

            switch (frame.BodyCase)
            {
                case Frame.BodyOneofCase.Ping:
                    await socket.SendAsync(new Frame { Pong = new Pong { TimestampMs = frame.Ping.TimestampMs } }, cancellationToken).ConfigureAwait(false);
                    break;
                case Frame.BodyOneofCase.Error:
                    // e.g. NOT_AUTHORIZED when an Admin disabled the device; the server closes the connection next.
                    _ui.OnStatusChanged(AgentStatus.Ended, frame.Error.Code);
                    break;
                default:
                    break;
            }
        }
    }
}
