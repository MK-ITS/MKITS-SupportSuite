// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Sessions;

/// <summary>Why <see cref="AgentSession.RunAsync"/> returned.</summary>
/// <param name="EndReason">Server end reason (e.g. <c>ended_by_supporter</c>) when the session ended normally.</param>
/// <param name="ErrorCode">One of <see cref="ErrorCodes"/> when the server refused or the connection could not be restored.</param>
public sealed record AgentSessionResult(string? EndReason, string? ErrorCode)
{
    public bool Succeeded => ErrorCode is null;
}

/// <summary>
/// One ad-hoc support session from the agent's point of view (docs/protocol.md, docs/session-lifecycle.md):
/// handshake and authentication with the session code and resuming after a short connection loss. The session
/// itself (consent, keys, payloads) is handled by <see cref="SessionEngine"/>.
/// </summary>
public sealed class AgentSession : ISessionContext, IAsyncDisposable
{
    private readonly AgentSessionOptions _options;
    private readonly IWebSocketConnector _connector;
    private readonly IAgentUserInterface _ui;
    private readonly SessionEngine _engine;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _endedByServer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrameSocket? _socket;
    private string? _resumeToken;
    private string? _endReason;

    public AgentSession(
        AgentSessionOptions options,
        IWebSocketConnector connector,
        IAgentUserInterface ui,
        IEnumerable<ISessionChannelHandler> handlers,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(handlers);

        _options = options;
        _connector = connector;
        _ui = ui;
        _engine = new SessionEngine(ui, handlers.ToList(), options.ServerUri.Host);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-assigned session id (after authentication).</summary>
    public string? SessionId => _engine.SessionId;

    /// <summary>
    /// Connects, authenticates with the code and runs until the session ends, the user ends it
    /// (<see cref="EndAsync"/>) or the connection cannot be restored.
    /// </summary>
    public async Task<AgentSessionResult> RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;

        _ui.OnStatusChanged(AgentStatus.Connecting, _options.ServerUri.Host);
        var first = await ConnectAndAuthenticateAsync(resume: false, token).ConfigureAwait(false);
        if (first is not null)
        {
            _ui.OnStatusChanged(AgentStatus.Ended, first);
            return new AgentSessionResult(null, first);
        }

        while (true)
        {
            var closedNormally = await ReceiveLoopAsync(token).ConfigureAwait(false);
            if (closedNormally || token.IsCancellationRequested)
            {
                _endedByServer.TrySetResult();
                _ui.OnStatusChanged(AgentStatus.Ended, _endReason);
                return new AgentSessionResult(_endReason ?? "ended_by_customer", null);
            }

            // Connection lost: keys are bound to the old connection's viewers; the server will re-announce them.
            _engine.ResetViewers();
            _ui.OnStatusChanged(AgentStatus.Reconnecting, null);
            var error = await ReconnectAsync(token).ConfigureAwait(false);
            if (error is not null)
            {
                _ui.OnStatusChanged(AgentStatus.Ended, error);
                return new AgentSessionResult(null, error);
            }
        }
    }

    /// <summary>
    /// The end user clicked "End session" (always available). Tells the server and waits briefly for its
    /// confirmation, so supporters see a clean end instead of a connection loss.
    /// </summary>
    public async Task EndAsync()
    {
        _endReason ??= "ended_by_customer";
        if (_socket is { } socket && _engine.SessionId is { } sessionId)
        {
            try
            {
                await socket.SendAsync(new Frame { EndSession = new EndSession { SessionId = sessionId, Reason = "ended_by_customer" } }, CancellationToken.None).ConfigureAwait(false);
                await _endedByServer.Task.WaitAsync(TimeSpan.FromSeconds(5), _timeProvider).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException or InvalidOperationException or TimeoutException)
            {
                // the connection is gone anyway
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> ReadyViewerIds => _engine.ReadyViewerIds;

    /// <inheritdoc />
    public Task SendAsync(string? viewerId, SessionPayload payload, CancellationToken cancellationToken) =>
        _engine.SendAsync(viewerId, payload, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_socket is { } socket)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }

        _engine.ResetViewers();
        _stop.Dispose();
    }

    // ------------------------------------------------------------------ connection

    /// <summary>Returns <c>null</c> on success, otherwise an error code.</summary>
    private async Task<string?> ConnectAndAuthenticateAsync(bool resume, CancellationToken cancellationToken)
    {
        FrameSocket socket;
        try
        {
            socket = new FrameSocket(await _connector.ConnectAsync(_options.AgentEndpoint, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or HttpRequestException or IOException)
        {
            return ErrorCodes.Internal;
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
            return ack?.Error?.Code ?? ErrorCodes.ProtocolMismatch;
        }

        var authenticate = resume
            ? new Authenticate { Resume = new Resume { SessionId = _engine.SessionId, Token = _resumeToken } }
            : new Authenticate { AdHocCode = new AdHocCode { Code = _options.Code, Machine = _options.Machine } };
        await socket.SendAsync(new Frame { Authenticate = authenticate }, cancellationToken).ConfigureAwait(false);

        var result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (result?.BodyCase != Frame.BodyOneofCase.AuthResult || !result.AuthResult.Ok)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            return result?.Error?.Code ?? ErrorCodes.AuthFailed;
        }

        _resumeToken = result.AuthResult.ResumeToken;
        _engine.Attach(socket);
        var previous = Interlocked.Exchange(ref _socket, socket);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> ReconnectAsync(CancellationToken cancellationToken)
    {
        if (_engine.SessionId is null || _resumeToken is null)
        {
            return ErrorCodes.AuthFailed; // lost before the session was assigned – a resume can never succeed
        }

        var deadline = _timeProvider.GetUtcNow() + _options.ReconnectWindow;
        var delay = TimeSpan.FromSeconds(1);
        string? lastError = ErrorCodes.Internal;

        while (_timeProvider.GetUtcNow() < deadline && !cancellationToken.IsCancellationRequested)
        {
            lastError = await ConnectAndAuthenticateAsync(resume: true, cancellationToken).ConfigureAwait(false);
            if (lastError is null)
            {
                return null;
            }

            if (lastError is ErrorCodes.AuthFailed or ErrorCodes.SessionEnded)
            {
                return lastError; // the session is gone – retrying cannot help
            }

            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(10).Ticks));
        }

        return lastError ?? ErrorCodes.Internal;
    }

    // ------------------------------------------------------------------ receive

    /// <summary>Returns <c>true</c> when the session ended (normally or by error), <c>false</c> on connection loss.</summary>
    private async Task<bool> ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _socket!;
        while (!cancellationToken.IsCancellationRequested)
        {
            Frame? frame;
            try
            {
                frame = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return true;
            }

            if (frame is null)
            {
                return _endReason is not null;
            }

            string? ended = null;
            if (await _engine.HandleAsync(frame, cancellationToken, reason => ended = reason).ConfigureAwait(false))
            {
                if (ended is not null)
                {
                    _endReason = ended;
                    return true;
                }

                continue;
            }

            switch (frame.BodyCase)
            {
                case Frame.BodyOneofCase.Ping:
                    await socket.SendAsync(new Frame { Pong = new Pong { TimestampMs = frame.Ping.TimestampMs } }, cancellationToken).ConfigureAwait(false);
                    break;
                case Frame.BodyOneofCase.Error:
                    _endReason = frame.Error.Code;
                    return true;
                default:
                    break; // unknown or irrelevant frames are ignored (forward compatibility)
            }
        }

        return true;
    }
}
