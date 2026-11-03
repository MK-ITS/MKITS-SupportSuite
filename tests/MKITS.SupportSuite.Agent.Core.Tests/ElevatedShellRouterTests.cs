// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Google.Protobuf;
using MKITS.SupportSuite.Agent.Core.Features;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Tests;

public sealed class ElevatedShellRouterTests
{
    private readonly RecordingHandler _inner = new();
    private readonly ShellHandlerTests.FakeShellHost _elevatedHost = new();
    private readonly RecordingContext _context = new();

    [Fact]
    public async Task ElevatedShells_RunLocally_EverythingElseGoesToTheInnerHandler()
    {
        await using var router = new ElevatedShellRouter(_inner, new ShellHandler(host: null, _elevatedHost));

        await Send(router, new SessionPayload { ShellOpen = new ShellOpen { ShellId = "admin", Cols = 80, Rows = 24, Elevated = true } });
        await Send(router, new SessionPayload { ShellOpen = new ShellOpen { ShellId = "user", Cols = 80, Rows = 24 } });
        await Send(router, new SessionPayload { ShellData = new ShellData { ShellId = "admin", Data = ByteString.CopyFromUtf8("whoami\r") } });
        await Send(router, new SessionPayload { ShellData = new ShellData { ShellId = "user", Data = ByteString.CopyFromUtf8("dir\r") } });
        await Send(router, new SessionPayload { ShellResize = new ShellResize { ShellId = "admin", Cols = 100, Rows = 30 } });
        await Send(router, new SessionPayload { ChatMessage = new ChatMessage { Text = "hi" } });
        await Send(router, new SessionPayload { ShellExit = new ShellExit { ShellId = "admin" } });
        await Send(router, new SessionPayload { ShellData = new ShellData { ShellId = "admin", Data = ByteString.CopyFromUtf8("late\r") } });

        var shell = Assert.Single(_elevatedHost.Started);
        Assert.Equal((100, 30), (shell.Columns, shell.Rows));
        Assert.True(shell.Disposed);
        Assert.Equal(
            ["ShellOpen:user", "ShellData:user", "ChatMessage:", "ShellData:admin"], // after the exit, "admin" is unknown again
            _inner.Received.Select(p => $"{p.BodyCase}:{ShellId(p)}"));
    }

    [Fact]
    public async Task ViewerEvents_ReachBothSides()
    {
        await using var router = new ElevatedShellRouter(_inner, new ShellHandler(host: null, _elevatedHost));
        await Send(router, new SessionPayload { ShellOpen = new ShellOpen { ShellId = "admin", Cols = 80, Rows = 24, Elevated = true } });

        await router.OnViewerReadyAsync(_context, "v1", CancellationToken.None);
        router.OnViewerGone("v1");
        for (var i = 0; i < 100 && !_elevatedHost.Started[0].Disposed; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(["ready:v1", "gone:v1"], _inner.Events);
        Assert.True(_elevatedHost.Started[0].Disposed);
    }

    private static string ShellId(SessionPayload payload) => payload.BodyCase switch
    {
        SessionPayload.BodyOneofCase.ShellOpen => payload.ShellOpen.ShellId,
        SessionPayload.BodyOneofCase.ShellData => payload.ShellData.ShellId,
        SessionPayload.BodyOneofCase.ShellResize => payload.ShellResize.ShellId,
        SessionPayload.BodyOneofCase.ShellExit => payload.ShellExit.ShellId,
        _ => string.Empty,
    };

    private Task Send(ElevatedShellRouter router, SessionPayload payload) => router.HandleAsync(_context, "v1", payload, CancellationToken.None);

    private sealed class RecordingHandler : ISessionChannelHandler
    {
        public List<SessionPayload> Received { get; } = [];

        public List<string> Events { get; } = [];

        public Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
        {
            Received.Add(payload);
            return Task.CompletedTask;
        }

        public Task OnViewerReadyAsync(ISessionContext context, string viewerId, CancellationToken cancellationToken)
        {
            Events.Add("ready:" + viewerId);
            return Task.CompletedTask;
        }

        public void OnViewerGone(string viewerId) => Events.Add("gone:" + viewerId);
    }
}
