// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Bridge;

/// <summary>
/// The user-session side of <see cref="UserSessionBridge"/>: runs the channel handlers that need the logged-on user
/// (chat, clipboard, files, shell) with the user's rights and drives the session window. Payloads are passed to and
/// from the service, which encrypts them for the viewers.
/// </summary>
public sealed class BridgeClient(Stream stream, IAgentUserInterface ui, IReadOnlyList<ISessionChannelHandler> handlers) : ISessionContext, IDisposable
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _readyViewers = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public IReadOnlyCollection<string> ReadyViewerIds => _readyViewers.Keys.ToList();

    /// <summary>Runs until the service closes the connection (session over) or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var message = await BridgeProtocol.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                await HandleAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidDataException or EndOfStreamException or FormatException
            or InvalidProtocolBufferException)
        {
            // the service is gone or sent garbage
        }
        finally
        {
            foreach (var viewerId in _readyViewers.Keys)
            {
                NotifyGone(viewerId);
            }

            _readyViewers.Clear();
        }
    }

    /// <inheritdoc />
    public Task SendAsync(string? viewerId, SessionPayload payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return WriteAsync(BridgeProtocol.Build(BridgeMessageType.Payload, w =>
        {
            w.Write(viewerId ?? string.Empty);
            var bytes = payload.ToByteArray();
            w.Write(bytes.Length);
            w.Write(bytes);
        }), cancellationToken);
    }

    /// <summary>The user clicked "End session".</summary>
    public Task EndSessionAsync(CancellationToken cancellationToken) =>
        WriteAsync(BridgeProtocol.Build(BridgeMessageType.EndSession), cancellationToken);

    public void Dispose() => _writeLock.Dispose();

    private async Task HandleAsync(byte[] message, CancellationToken cancellationToken)
    {
        using var reader = new BinaryReader(new MemoryStream(message));
        switch ((BridgeMessageType)reader.ReadByte())
        {
            case BridgeMessageType.SessionAssigned:
                ui.OnSessionAssigned(BridgeProtocol.ReadString(reader), (ConsentPolicy)reader.ReadInt32());
                break;
            case BridgeMessageType.Status:
                var status = (AgentStatus)reader.ReadInt32();
                var detail = BridgeProtocol.ReadString(reader);
                ui.OnStatusChanged(status, detail.Length == 0 ? null : detail);
                break;
            case BridgeMessageType.Supporters:
                ui.OnSupportersChanged(BridgeProtocol.ReadStrings(reader));
                break;
            case BridgeMessageType.SecurityCode:
                ui.OnSecurityCode(BridgeProtocol.ReadString(reader), BridgeProtocol.ReadString(reader));
                break;
            case BridgeMessageType.ViewerReady:
                var ready = BridgeProtocol.ReadString(reader);
                _readyViewers[ready] = 0;
                foreach (var handler in handlers)
                {
                    await handler.OnViewerReadyAsync(this, ready, cancellationToken).ConfigureAwait(false);
                }

                break;
            case BridgeMessageType.ViewerGone:
                var gone = BridgeProtocol.ReadString(reader);
                if (_readyViewers.TryRemove(gone, out _))
                {
                    NotifyGone(gone);
                }

                break;
            case BridgeMessageType.Payload:
                var viewerId = BridgeProtocol.ReadString(reader);
                var payload = SessionPayload.Parser.ParseFrom(reader.ReadBytes(Math.Clamp(reader.ReadInt32(), 0, BridgeProtocol.MaxMessageSize)));
                foreach (var handler in handlers)
                {
                    await handler.HandleAsync(this, viewerId, payload, cancellationToken).ConfigureAwait(false);
                }

                break;
            case BridgeMessageType.ConsentRequest:
                var id = reader.ReadInt32();
                var prompt = new ConsentPrompt(
                    ServerHost: BridgeProtocol.ReadString(reader),
                    OperatorName: BridgeProtocol.ReadString(reader),
                    SupporterDisplayName: BridgeProtocol.ReadString(reader),
                    Permissions: BridgeProtocol.ReadStrings(reader));
                _ = AnswerConsentAsync(id, prompt, cancellationToken); // never block the read loop on a dialog
                break;
            default:
                break; // unknown messages are ignored (newer service)
        }
    }

    private async Task AnswerConsentAsync(int id, ConsentPrompt prompt, CancellationToken cancellationToken)
    {
        bool granted;
        try
        {
            granted = await ui.RequestConsentAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            granted = false;
        }

        await WriteAsync(BridgeProtocol.Build(BridgeMessageType.ConsentAnswer, w =>
        {
            w.Write(id);
            w.Write(granted);
        }), CancellationToken.None).ConfigureAwait(false);
    }

    private void NotifyGone(string viewerId)
    {
        foreach (var handler in handlers)
        {
            handler.OnViewerGone(viewerId);
        }
    }

    private async Task WriteAsync(byte[] message, CancellationToken cancellationToken)
    {
        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await BridgeProtocol.WriteAsync(stream, message, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // the service closed the connection
        }
    }
}
