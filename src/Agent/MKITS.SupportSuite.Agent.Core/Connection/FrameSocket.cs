// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers;
using System.Net.WebSockets;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Connection;

/// <summary>
/// Sends and receives protocol frames over one WebSocket. Sending is serialized (WebSocket allows one
/// concurrent send); receiving must happen on a single loop.
/// </summary>
public sealed class FrameSocket(WebSocket socket) : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public bool IsOpen => socket.State == WebSocketState.Open;

    public async Task SendAsync(Frame frame, CancellationToken cancellationToken)
    {
        var bytes = FrameCodec.Encode(frame);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Next frame, or <c>null</c> if the connection closed or delivered something undecodable.</summary>
    public async Task<Frame?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(FrameCodec.MaxMessageSize);
        try
        {
            var length = 0;
            while (true)
            {
                if (length >= FrameCodec.MaxMessageSize)
                {
                    return null;
                }

                var result = await socket.ReceiveAsync(buffer.AsMemory(length, FrameCodec.MaxMessageSize - length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    return null;
                }

                length += result.Count;
                if (result.EndOfMessage)
                {
                    break;
                }
            }

            return FrameCodec.TryDecode(buffer.AsSpan(0, length), out var frame) ? frame : null;
        }
        catch (WebSocketException)
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // best effort
            }
        }

        socket.Dispose();
        _sendLock.Dispose();
    }
}
