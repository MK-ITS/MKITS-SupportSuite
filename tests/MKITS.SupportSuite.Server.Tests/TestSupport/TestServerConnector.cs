// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net.WebSockets;
using Microsoft.AspNetCore.TestHost;
using MKITS.SupportSuite.Agent.Core.Connection;
using MKITS.SupportSuite.Common.Protocol;

namespace MKITS.SupportSuite.Server.Tests.TestSupport;

/// <summary>Connects the real agent core to the in-memory test server.</summary>
public sealed class TestServerConnector(TestServer server, string remoteIp = "198.51.100.30") : IWebSocketConnector
{
    public Task<WebSocket> ConnectAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var client = server.CreateWebSocketClient();
        client.SubProtocols.Add(FrameCodec.SubProtocol);
        client.ConfigureRequest = request => request.Headers[MkitsWebApplicationFactory.TestRemoteIpHeader] = remoteIp;
        return client.ConnectAsync(new Uri(server.BaseAddress, uri.PathAndQuery), cancellationToken);
    }
}
