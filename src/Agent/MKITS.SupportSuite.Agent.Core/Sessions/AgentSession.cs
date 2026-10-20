// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Common.Crypto;
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
/// handshake and authentication, consent dialog, end-to-end key exchange per supporter, decrypting and dispatching
/// session-plane payloads to the channel handlers, and resuming after a short connection loss.
/// </summary>
public sealed class AgentSession : ISessionContext, IAsyncDisposable
{
    private readonly AgentSessionOptions _options;
    private readonly IWebSocketConnector _connector;
    private readonly IAgentUserInterface _ui;
    private readonly IReadOnlyList<ISessionChannelHandler> _handlers;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, ViewerState> _viewers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _endedByServer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private FrameSocket? _socket;
    private string? _sessionId;
    private string? _resumeToken;
    private string _operatorName = string.Empty;
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
        _handlers = handlers.ToList();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Server-assigned session id (after authentication).</summary>
    public string? SessionId => _sessionId;

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
            ResetViewers();
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
        if (_socket is { } socket && _sessionId is not null)
        {
            try
            {
                await socket.SendAsync(new Frame { EndSession = new EndSession { SessionId = _sessionId, Reason = "ended_by_customer" } }, CancellationToken.None).ConfigureAwait(false);
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
    public async Task SendAsync(string? viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var socket = _socket;
        if (socket is null || _sessionId is null)
        {
            return;
        }

        var channel = (uint)PayloadChannels.For(payload);
        var plaintext = payload.ToByteArray();
        var targets = viewerId is null
            ? _viewers.Values.Where(v => v.Cipher is not null).ToList()
            : _viewers.TryGetValue(viewerId, out var one) && one.Cipher is not null ? [one] : [];

        foreach (var viewer in targets)
        {
            // Encrypt and send under the viewer's lock: sequence numbers must reach the wire in order.
            await viewer.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (sequence, ciphertext) = viewer.Cipher!.Encrypt(channel, plaintext);
                await socket.SendAsync(new Frame
                {
                    SessionData = new SessionData
                    {
                        SessionId = _sessionId,
                        ViewerId = viewer.Id,
                        Channel = channel,
                        Sequence = sequence,
                        Ciphertext = ByteString.CopyFrom(ciphertext),
                    },
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                viewer.SendLock.Release();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_socket is { } socket)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
        }

        ResetViewers();
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
            ? new Authenticate { Resume = new Resume { SessionId = _sessionId, Token = _resumeToken } }
            : new Authenticate { AdHocCode = new AdHocCode { Code = _options.Code, Machine = _options.Machine } };
        await socket.SendAsync(new Frame { Authenticate = authenticate }, cancellationToken).ConfigureAwait(false);

        var result = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (result?.BodyCase != Frame.BodyOneofCase.AuthResult || !result.AuthResult.Ok)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            return result?.Error?.Code ?? ErrorCodes.AuthFailed;
        }

        _resumeToken = result.AuthResult.ResumeToken;
        var previous = Interlocked.Exchange(ref _socket, socket);
        if (previous is not null)
        {
            await previous.DisposeAsync().ConfigureAwait(false);
        }

        return null;
    }

    private async Task<string?> ReconnectAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is null || _resumeToken is null)
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

            switch (frame.BodyCase)
            {
                case Frame.BodyOneofCase.SessionAssigned:
                    _sessionId = frame.SessionAssigned.SessionId;
                    _operatorName = frame.SessionAssigned.OperatorName;
                    _ui.OnStatusChanged(AgentStatus.WaitingForSupporter, _operatorName);
                    break;
                case Frame.BodyOneofCase.SessionState:
                    if (OnSessionState(frame.SessionState))
                    {
                        return true;
                    }

                    break;
                case Frame.BodyOneofCase.ViewerJoined:
                    _viewers[frame.ViewerJoined.ViewerId] = new ViewerState(frame.ViewerJoined.ViewerId, frame.ViewerJoined.DisplayName);
                    PublishSupporters();
                    break;
                case Frame.BodyOneofCase.ViewerLeft:
                    if (_viewers.TryRemove(frame.ViewerLeft.ViewerId, out var left))
                    {
                        NotifyViewerGone(left.Id);
                        left.Dispose();
                    }

                    PublishSupporters();
                    break;
                case Frame.BodyOneofCase.ConsentRequest:
                    // Do not block the receive loop while the dialog is open.
                    _ = AskConsentAsync(frame.ConsentRequest, cancellationToken);
                    break;
                case Frame.BodyOneofCase.KeyExchange:
                    await OnKeyExchangeAsync(frame.KeyExchange, cancellationToken).ConfigureAwait(false);
                    break;
                case Frame.BodyOneofCase.SessionData:
                    await OnSessionDataAsync(frame.SessionData, cancellationToken).ConfigureAwait(false);
                    break;
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

    /// <summary>Returns <c>true</c> when the session ended.</summary>
    private bool OnSessionState(SessionState state)
    {
        switch (state.Status)
        {
            case SessionStatus.WaitingForViewer:
                _ui.OnStatusChanged(AgentStatus.WaitingForSupporter, _operatorName);
                break;
            case SessionStatus.AwaitingConsent:
                _ui.OnStatusChanged(AgentStatus.AwaitingConsent, null);
                break;
            case SessionStatus.Active:
                _ui.OnStatusChanged(AgentStatus.Active, null);
                break;
            case SessionStatus.Ended:
                _endReason = string.IsNullOrEmpty(state.EndReason) ? "ended" : state.EndReason;
                return true;
            default:
                break;
        }

        return false;
    }

    private async Task AskConsentAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        bool granted;
        try
        {
            granted = await _ui.RequestConsentAsync(
                new ConsentPrompt(_options.ServerUri.Host, _operatorName, request.SupporterDisplayName, request.RequestedPermissions.ToList()),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_socket is { } socket)
        {
            try
            {
                await socket.SendAsync(new Frame
                {
                    ConsentResponse = new ConsentResponse { SessionId = request.SessionId, ViewerId = request.ViewerId, Granted = granted },
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
            {
                // connection lost; the server asks again after a resume
            }
        }
    }

    /// <summary>The viewer starts the key exchange; the agent answers with its own ephemeral key.</summary>
    private async Task OnKeyExchangeAsync(KeyExchange exchange, CancellationToken cancellationToken)
    {
        if (_sessionId is null || !_viewers.TryGetValue(exchange.ViewerId, out var viewer))
        {
            return;
        }

        using var own = new E2EKeyPair();
        var viewerKey = exchange.EphemeralPublicKey.ToByteArray();
        SessionCipher cipher;
        try
        {
            cipher = SessionCrypto.Establish(E2ERole.Agent, own, _sessionId, own.PublicKey, viewerKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return; // invalid key from the viewer – no session plane for it
        }

        viewer.ReplaceCipher(cipher);
        await _socket!.SendAsync(new Frame
        {
            KeyExchange = new KeyExchange { SessionId = _sessionId, ViewerId = viewer.Id, EphemeralPublicKey = ByteString.CopyFrom(own.PublicKey) },
        }, cancellationToken).ConfigureAwait(false);

        _ui.OnSecurityCode(viewer.DisplayName, SessionCrypto.ComputeSecurityCode(_sessionId, own.PublicKey, viewerKey));

        foreach (var handler in _handlers)
        {
            await handler.OnViewerReadyAsync(this, viewer.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnSessionDataAsync(SessionData data, CancellationToken cancellationToken)
    {
        if (!_viewers.TryGetValue(data.ViewerId, out var viewer) || viewer.Cipher is not { } cipher)
        {
            return;
        }

        var plaintext = cipher.Decrypt(data.Channel, data.Sequence, data.Ciphertext.Span);
        if (plaintext is null)
        {
            return; // forged, replayed or for an old key
        }

        SessionPayload payload;
        try
        {
            payload = SessionPayload.Parser.ParseFrom(plaintext);
        }
        catch (InvalidProtocolBufferException)
        {
            return;
        }

        if (!PayloadChannels.Matches(data.Channel, payload))
        {
            return;
        }

        foreach (var handler in _handlers)
        {
            await handler.HandleAsync(this, viewer.Id, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private void PublishSupporters() => _ui.OnSupportersChanged(_viewers.Values.Select(v => v.DisplayName).ToList());

    private void NotifyViewerGone(string viewerId)
    {
        foreach (var handler in _handlers)
        {
            handler.OnViewerGone(viewerId);
        }
    }

    private void ResetViewers()
    {
        foreach (var viewer in _viewers.Values)
        {
            NotifyViewerGone(viewer.Id);
            viewer.Dispose();
        }

        _viewers.Clear();
        PublishSupporters();
    }

    /// <summary>One connected supporter and its end-to-end cipher.</summary>
    private sealed class ViewerState(string id, string displayName) : IDisposable
    {
        private SessionCipher? _cipher;

        public string Id { get; } = id;

        public string DisplayName { get; } = displayName;

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public SessionCipher? Cipher => Volatile.Read(ref _cipher);

        public void ReplaceCipher(SessionCipher cipher) => Interlocked.Exchange(ref _cipher, cipher)?.Dispose();

        public void Dispose()
        {
            Interlocked.Exchange(ref _cipher, null)?.Dispose();
            SendLock.Dispose();
        }
    }
}
