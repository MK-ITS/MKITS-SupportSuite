// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using MKITS.SupportSuite.Common.Protocol;
using Frame = MKITS.SupportSuite.Common.Protocol.V0.Frame;
using PeerRole = MKITS.SupportSuite.Common.Protocol.V0.PeerRole;

namespace MKITS.SupportSuite.Server.Features.Relay;

/// <summary>Outcome of <see cref="PeerConnection.ReceiveAsync"/>.</summary>
public enum ReceiveStatus
{
    Frame,
    Closed,
    TooLarge,
    Malformed,
}

/// <summary>
/// One WebSocket peer (agent or viewer). Receiving happens on the connection's own loop; sending is decoupled through
/// an outbound queue, so a slow peer can never block the relay. Backpressure (docs/protocol.md §5):
/// droppable frames (video) are discarded above <see cref="DropThreshold"/> queued bytes, and a peer whose queue
/// exceeds <see cref="DisconnectThreshold"/> is disconnected.
/// </summary>
public sealed class PeerConnection : IDisposable
{
    internal const long DropThreshold = 4L * 1024 * 1024;
    internal const long DisconnectThreshold = 16L * 1024 * 1024;

    private const int InitialReceiveBuffer = 16 * 1024;

    private readonly WebSocket _socket;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _abort = new();
    private long _queuedBytes;
    private long _lastReceivedTicks;
    private int _aborted;

    public PeerConnection(WebSocket socket, PeerRole role, IPAddress? remoteAddress, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _socket = socket;
        _timeProvider = timeProvider;
        Role = role;
        RemoteAddress = remoteAddress;
        _lastReceivedTicks = timeProvider.GetUtcNow().UtcTicks;
        LastPingSent = timeProvider.GetUtcNow();
    }

    /// <summary>Random identifier, used as <c>peer_id</c> / <c>viewer_id</c> on the wire.</summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public PeerRole Role { get; }

    public IPAddress? RemoteAddress { get; }

    /// <summary>Time of the last message received from the peer (any message counts as a heartbeat).</summary>
    public DateTimeOffset LastReceived => new(Interlocked.Read(ref _lastReceivedTicks), TimeSpan.Zero);

    /// <summary>Maintained by the heartbeat loop.</summary>
    public DateTimeOffset LastPingSent { get; set; }

    /// <summary>Cancelled when the connection is aborted.</summary>
    public CancellationToken Aborted => _abort.Token;

    public long QueuedBytes => Interlocked.Read(ref _queuedBytes);

    /// <summary>
    /// Queues a frame. Returns <c>false</c> if the frame was dropped (droppable and the queue is full, or the
    /// connection is closing). Never blocks.
    /// </summary>
    public bool Send(Frame frame, bool droppable = false)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (Volatile.Read(ref _aborted) != 0)
        {
            return false;
        }

        var bytes = FrameCodec.Encode(frame);
        var queued = Interlocked.Read(ref _queuedBytes);
        if (droppable && queued > DropThreshold)
        {
            return false;
        }

        if (queued + bytes.Length > DisconnectThreshold)
        {
            Abort();
            return false;
        }

        Interlocked.Add(ref _queuedBytes, bytes.Length);
        if (!_outbound.Writer.TryWrite(bytes))
        {
            Interlocked.Add(ref _queuedBytes, -bytes.Length);
            return false;
        }

        return true;
    }

    /// <summary>Sends everything queued so far, then closes the WebSocket normally. Further sends are ignored.</summary>
    public void Complete() => _outbound.Writer.TryComplete();

    /// <summary>Drops the connection immediately.</summary>
    public void Abort()
    {
        if (Interlocked.Exchange(ref _aborted, 1) != 0)
        {
            return;
        }

        _outbound.Writer.TryComplete();
        _abort.Cancel();
        _socket.Abort();
    }

    /// <summary>Writes queued frames to the socket until <see cref="Complete"/> or <see cref="Abort"/>.</summary>
    public async Task RunSenderAsync()
    {
        try
        {
            await foreach (var message in _outbound.Reader.ReadAllAsync(_abort.Token).ConfigureAwait(false))
            {
                await _socket.SendAsync(message, WebSocketMessageType.Binary, endOfMessage: true, _abort.Token).ConfigureAwait(false);
                Interlocked.Add(ref _queuedBytes, -message.Length);
            }

            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // aborted
        }
        catch (WebSocketException)
        {
            // peer went away
        }
        finally
        {
            Abort();
        }
    }

    /// <summary>
    /// Reads one WebSocket message and decodes it. Messages are assembled up to <see cref="FrameCodec.MaxMessageSize"/>
    /// in a pooled buffer that grows on demand, so idle connections stay cheap.
    /// </summary>
    public async Task<(ReceiveStatus Status, Frame? Frame)> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(InitialReceiveBuffer);
        try
        {
            var length = 0;
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (buffer.Length >= FrameCodec.MaxMessageSize)
                    {
                        return (ReceiveStatus.TooLarge, null);
                    }

                    var larger = ArrayPool<byte>.Shared.Rent(Math.Min(buffer.Length * 2, FrameCodec.MaxMessageSize));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                var limit = Math.Min(buffer.Length, FrameCodec.MaxMessageSize);
                var result = await _socket.ReceiveAsync(buffer.AsMemory(length, limit - length), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return (ReceiveStatus.Closed, null);
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    return (ReceiveStatus.Malformed, null);
                }

                length += result.Count;
                Interlocked.Exchange(ref _lastReceivedTicks, _timeProvider.GetUtcNow().UtcTicks);

                if (result.EndOfMessage)
                {
                    break;
                }

                if (length >= FrameCodec.MaxMessageSize)
                {
                    return (ReceiveStatus.TooLarge, null);
                }
            }

            return FrameCodec.TryDecode(buffer.AsSpan(0, length), out var frame)
                ? (ReceiveStatus.Frame, frame)
                : (ReceiveStatus.Malformed, null);
        }
        catch (WebSocketException)
        {
            return (ReceiveStatus.Closed, null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        Abort();
        _abort.Dispose();
    }
}
