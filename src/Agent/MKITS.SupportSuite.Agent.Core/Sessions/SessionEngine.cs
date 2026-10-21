// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Common.Crypto;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Sessions;

/// <summary>
/// The session part shared by ad-hoc and permanent agents: viewers, consent dialog, end-to-end key exchange per
/// viewer, decrypting and dispatching session-plane payloads to the channel handlers, and the viewers' permissions
/// (checked here as well as on the relay). The owner runs the connection and feeds the frames in.
/// </summary>
internal sealed class SessionEngine : ISessionContext
{
    private readonly IAgentUserInterface _ui;
    private readonly IReadOnlyList<ISessionChannelHandler> _handlers;
    private readonly string _serverHost;
    private readonly Func<byte[], byte[]>? _signEphemeralKey;
    private readonly ConcurrentDictionary<string, ViewerState> _viewers = new(StringComparer.Ordinal);
    private FrameSocket? _socket;
    private string? _sessionId;
    private string _operatorName = string.Empty;

    /// <summary>Permanent devices pass <c>signEphemeralKey</c> to sign their ephemeral key with the device key (docs/protocol.md §6.5).</summary>
    public SessionEngine(IAgentUserInterface ui, IReadOnlyList<ISessionChannelHandler> handlers, string serverHost, Func<byte[], byte[]>? signEphemeralKey = null)
    {
        _ui = ui;
        _handlers = handlers;
        _serverHost = serverHost;
        _signEphemeralKey = signEphemeralKey;
    }

    public string? SessionId => _sessionId;

    public string OperatorName => _operatorName;

    /// <inheritdoc />
    public IReadOnlyCollection<string> ReadyViewerIds => _viewers.Values.Where(v => v.Cipher is not null).Select(v => v.Id).ToList();

    /// <summary>The connection frames are sent on (replaced after a reconnect).</summary>
    public void Attach(FrameSocket socket) => _socket = socket;

    /// <summary>
    /// Handles a session frame. Returns <c>true</c> if the frame was one of the session frames; <c>onEnded</c> is
    /// called with the end reason when the server ended the session.
    /// </summary>
    public async Task<bool> HandleAsync(Frame frame, CancellationToken cancellationToken, Action<string>? onEnded = null)
    {
        switch (frame.BodyCase)
        {
            case Frame.BodyOneofCase.SessionAssigned:
                if (_sessionId != frame.SessionAssigned.SessionId)
                {
                    ResetViewers();
                }

                _sessionId = frame.SessionAssigned.SessionId;
                _operatorName = frame.SessionAssigned.OperatorName;
                _ui.OnSessionAssigned(_operatorName, frame.SessionAssigned.ConsentPolicy);
                _ui.OnStatusChanged(AgentStatus.WaitingForSupporter, _operatorName);
                return true;
            case Frame.BodyOneofCase.SessionState:
                OnSessionState(frame.SessionState, onEnded);
                return true;
            case Frame.BodyOneofCase.ViewerJoined:
                var joined = frame.ViewerJoined;
                _viewers[joined.ViewerId] = new ViewerState(joined.ViewerId, joined.DisplayName, ParsePermissions(joined.Permissions));
                PublishSupporters();
                return true;
            case Frame.BodyOneofCase.ViewerLeft:
                if (_viewers.TryRemove(frame.ViewerLeft.ViewerId, out var left))
                {
                    NotifyViewerGone(left.Id);
                    left.Dispose();
                }

                PublishSupporters();
                return true;
            case Frame.BodyOneofCase.ConsentRequest:
                // Do not block the receive loop while the dialog is open.
                _ = AskConsentAsync(frame.ConsentRequest, cancellationToken);
                return true;
            case Frame.BodyOneofCase.KeyExchange:
                await OnKeyExchangeAsync(frame.KeyExchange, cancellationToken).ConfigureAwait(false);
                return true;
            case Frame.BodyOneofCase.SessionData:
                await OnSessionDataAsync(frame.SessionData, cancellationToken).ConfigureAwait(false);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The session is over (ended or left): forget the session and all viewers.</summary>
    public void EndSession()
    {
        ResetViewers();
        _sessionId = null;
        _operatorName = string.Empty;
    }

    /// <inheritdoc />
    public async Task SendAsync(string? viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var socket = _socket;
        var sessionId = _sessionId;
        if (socket is null || sessionId is null)
        {
            return;
        }

        var channel = (uint)PayloadChannels.For(payload);
        var required = ChannelPermissions.Required(channel);
        var plaintext = payload.ToByteArray();
        var targets = viewerId is null
            ? _viewers.Values.Where(v => v.Cipher is not null && v.Permissions.HasFlag(required)).ToList()
            : _viewers.TryGetValue(viewerId, out var one) && one.Cipher is not null && one.Permissions.HasFlag(required) ? [one] : [];

        foreach (var viewer in targets)
        {
            // Encrypt and send under the viewer's lock: sequence numbers must reach the wire in order.
            await viewer.SendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (viewer.Cipher is not { } cipher)
                {
                    continue;
                }

                var (sequence, ciphertext) = cipher.Encrypt(channel, plaintext);
                await socket.SendAsync(new Frame
                {
                    SessionData = new SessionData
                    {
                        SessionId = sessionId,
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

    /// <summary>Keys are bound to a connection: after a connection loss the server announces the viewers again.</summary>
    public void ResetViewers()
    {
        foreach (var viewer in _viewers.Values)
        {
            NotifyViewerGone(viewer.Id);
            viewer.Dispose();
        }

        _viewers.Clear();
        PublishSupporters();
    }

    /// <summary>
    /// Servers before protocol 0.3 do not send permissions; their sessions are ad-hoc with full rights. The relay
    /// enforces the permissions anyway – this is defense in depth.
    /// </summary>
    private static ViewerPermissions ParsePermissions(IReadOnlyCollection<string> names) =>
        names.Count == 0 ? ViewerPermissions.All : ChannelPermissions.Parse(names);

    private void OnSessionState(SessionState state, Action<string>? onEnded)
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
                onEnded?.Invoke(string.IsNullOrEmpty(state.EndReason) ? "ended" : state.EndReason);
                break;
            default:
                break;
        }
    }

    private async Task AskConsentAsync(ConsentRequest request, CancellationToken cancellationToken)
    {
        bool granted;
        try
        {
            granted = await _ui.RequestConsentAsync(
                new ConsentPrompt(_serverHost, _operatorName, request.SupporterDisplayName, request.RequestedPermissions.ToList()),
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
        if (_sessionId is not { } sessionId || _socket is not { } socket || !_viewers.TryGetValue(exchange.ViewerId, out var viewer))
        {
            return;
        }

        using var own = new E2EKeyPair();
        var viewerKey = exchange.EphemeralPublicKey.ToByteArray();
        SessionCipher cipher;
        try
        {
            cipher = SessionCrypto.Establish(E2ERole.Agent, own, sessionId, own.PublicKey, viewerKey);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return; // invalid key from the viewer – no session plane for it
        }

        viewer.ReplaceCipher(cipher);
        var answer = new KeyExchange { SessionId = sessionId, ViewerId = viewer.Id, EphemeralPublicKey = ByteString.CopyFrom(own.PublicKey) };
        if (_signEphemeralKey is { } sign)
        {
            answer.DeviceSignature = ByteString.CopyFrom(sign(own.PublicKey));
        }

        await socket.SendAsync(new Frame { KeyExchange = answer }, cancellationToken).ConfigureAwait(false);

        _ui.OnSecurityCode(viewer.DisplayName, SessionCrypto.ComputeSecurityCode(sessionId, own.PublicKey, viewerKey));

        foreach (var handler in _handlers)
        {
            await handler.OnViewerReadyAsync(this, viewer.Id, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task OnSessionDataAsync(SessionData data, CancellationToken cancellationToken)
    {
        if (!_viewers.TryGetValue(data.ViewerId, out var viewer) || viewer.Cipher is not { } cipher
            || !viewer.Permissions.HasFlag(ChannelPermissions.Required(data.Channel)))
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

    /// <summary>One connected supporter, its permissions and its end-to-end cipher.</summary>
    private sealed class ViewerState(string id, string displayName, ViewerPermissions permissions) : IDisposable
    {
        private SessionCipher? _cipher;

        public string Id { get; } = id;

        public string DisplayName { get; } = displayName;

        public ViewerPermissions Permissions { get; } = permissions;

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
