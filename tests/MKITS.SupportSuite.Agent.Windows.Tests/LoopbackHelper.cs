// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.IO.Pipes;
using MKITS.SupportSuite.Agent.Windows.Elevation;

namespace MKITS.SupportSuite.Agent.Windows.Tests;

/// <summary>A helper on a local pipe in the test process, serving with <see cref="HelperRequestHandler"/> like the real one.</summary>
internal sealed class LoopbackHelper : IAsyncDisposable
{
    private readonly NamedPipeServerStream _server;
    private readonly Task _serving;

    private LoopbackHelper(NamedPipeServerStream server, HelperClient client, Task serving)
    {
        _server = server;
        Client = client;
        _serving = serving;
    }

    public HelperClient Client { get; }

    public static async Task<LoopbackHelper> StartAsync(HelperRequestHandler handler)
    {
        var name = $"mkits-test-{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), pipe.ConnectAsync());
        var serving = Task.Run(async () =>
        {
            while (await HelperProtocol.ReadAsync(server, CancellationToken.None) is { } message)
            {
                if (handler.Handle(message) is { Length: > 0 } response)
                {
                    await HelperProtocol.WriteAsync(server, response, CancellationToken.None);
                }
            }
        });
        return new LoopbackHelper(server, new HelperClient(pipe), serving);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _serving;
        await _server.DisposeAsync();
    }
}
