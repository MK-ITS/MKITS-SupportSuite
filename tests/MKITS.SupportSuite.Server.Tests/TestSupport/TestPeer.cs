// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net.WebSockets;
using Microsoft.AspNetCore.TestHost;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Server.Tests.TestSupport;

/// <summary>A protocol peer (agent or viewer) talking to the test server over a real WebSocket.</summary>
public sealed class TestPeer : IAsyncDisposable
{
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private readonly WebSocket _socket;

    private TestPeer(WebSocket socket) => _socket = socket;

    public WebSocketState State => _socket.State;

    public static async Task<TestPeer> ConnectAsync(TestServer server, string path, string remoteIp = "198.51.100.20")
    {
        ArgumentNullException.ThrowIfNull(server);

        var client = server.CreateWebSocketClient();
        client.SubProtocols.Add(FrameCodec.SubProtocol);
        client.ConfigureRequest = request => request.Headers[MkitsWebApplicationFactory.TestRemoteIpHeader] = remoteIp;
        var socket = await client.ConnectAsync(new Uri(server.BaseAddress, path), CancellationToken.None);
        return new TestPeer(socket);
    }

    /// <summary>Connects as an agent, says Hello and authenticates with a session code.</summary>
    public static async Task<(TestPeer Peer, AuthResult Auth, SessionAssigned Assigned)> ConnectAgentAsync(TestServer server, string code)
    {
        var peer = await ConnectAsync(server, "/ws/agent");
        await peer.HelloAsync(PeerRole.Agent);
        await peer.SendAsync(new Frame
        {
            Authenticate = new Authenticate { AdHocCode = new AdHocCode { Code = code, Machine = new MachineInfo { MachineName = "CUSTOMER-PC" } } },
        });
        var auth = (await peer.ExpectAsync(Frame.BodyOneofCase.AuthResult)).AuthResult;
        var assigned = (await peer.ExpectAsync(Frame.BodyOneofCase.SessionAssigned)).SessionAssigned;
        return (peer, auth, assigned);
    }

    /// <summary>Connects as a viewer with a ticket.</summary>
    public static async Task<(TestPeer Peer, AuthResult Auth)> ConnectViewerAsync(TestServer server, string ticket)
    {
        var peer = await ConnectAsync(server, "/ws/viewer");
        await peer.HelloAsync(PeerRole.Viewer);
        await peer.SendAsync(new Frame { Authenticate = new Authenticate { ViewerTicket = new ViewerTicket { Ticket = ticket } } });
        var auth = (await peer.ExpectAsync(Frame.BodyOneofCase.AuthResult)).AuthResult;
        return (peer, auth);
    }

    public async Task<HelloAck> HelloAsync(PeerRole role, uint major = ProtocolVersion.Major)
    {
        await SendAsync(new Frame
        {
            Hello = new Hello { ProtocolMajor = major, ProtocolMinor = ProtocolVersion.Minor, Role = role, Platform = "test", ProductVersion = "test" },
        });
        return (await ExpectAsync(Frame.BodyOneofCase.HelloAck)).HelloAck;
    }

    public Task SendAsync(Frame frame) =>
        _socket.SendAsync(FrameCodec.Encode(frame), WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

    public Task SendRawAsync(byte[] data, WebSocketMessageType type = WebSocketMessageType.Binary) =>
        _socket.SendAsync(data, type, endOfMessage: true, CancellationToken.None);

    /// <summary>Next frame, skipping heartbeat pings. Returns <c>null</c> when the server closed the connection.</summary>
    public async Task<Frame?> ReceiveAsync()
    {
        using var timeout = new CancellationTokenSource(ReceiveTimeout);
        var buffer = new byte[FrameCodec.MaxMessageSize + 1024];
        while (true)
        {
            var length = 0;
            bool endOfMessage;
            do
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(length), timeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
                {
                    return null; // connection dropped without a close handshake
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                length += result.Count;
                endOfMessage = result.EndOfMessage;
            }
            while (!endOfMessage);

            Assert.True(FrameCodec.TryDecode(buffer.AsSpan(0, length), out var frame), "server sent an undecodable frame");
            if (frame.BodyCase != Frame.BodyOneofCase.Ping)
            {
                return frame;
            }
        }
    }

    /// <summary>Next frame, which must be of the given type.</summary>
    public async Task<Frame> ExpectAsync(Frame.BodyOneofCase expected)
    {
        var frame = await ReceiveAsync();
        Assert.NotNull(frame);
        Assert.True(frame.BodyCase == expected, $"expected {expected}, got {frame.BodyCase}: {frame}");
        return frame;
    }

    /// <summary>Skips frames until one of the given type arrives (e.g. ignoring state notifications).</summary>
    public async Task<Frame> ExpectEventuallyAsync(Frame.BodyOneofCase expected, Func<Frame, bool>? predicate = null)
    {
        for (var i = 0; i < 20; i++)
        {
            var frame = await ReceiveAsync();
            Assert.True(frame is not null, $"connection closed while waiting for {expected}");
            if (frame.BodyCase == expected && (predicate is null || predicate(frame)))
            {
                return frame;
            }
        }

        throw new Xunit.Sdk.XunitException($"{expected} did not arrive");
    }

    /// <summary>Expects an <c>Error</c> with the code, followed by the server closing the connection.</summary>
    public async Task ExpectErrorAndCloseAsync(string code)
    {
        var error = await ExpectEventuallyAsync(Frame.BodyOneofCase.Error);
        Assert.Equal(code, error.Error.Code);
        Assert.Null(await ReceiveAsync());
    }

    /// <summary>Drops the connection without a close handshake (simulates a network failure).</summary>
    public void Abort() => _socket.Abort();

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                // already gone
            }
        }

        _socket.Dispose();
    }
}
