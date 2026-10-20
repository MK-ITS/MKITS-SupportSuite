// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net.WebSockets;
using MKITS.SupportSuite.Common.Protocol;

namespace MKITS.SupportSuite.Agent.Core.Connection;

/// <summary>Opens the WebSocket to the server. Replaceable for tests (in-memory test server).</summary>
public interface IWebSocketConnector
{
    Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>Production connector: TLS WebSocket with the <c>mkits.v0</c> sub-protocol.</summary>
public sealed class ClientWebSocketConnector : IWebSocketConnector
{
    public async Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.AddSubProtocol(FrameCodec.SubProtocol);
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
