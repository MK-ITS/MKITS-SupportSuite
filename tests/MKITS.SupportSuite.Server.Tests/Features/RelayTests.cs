// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using Google.Protobuf;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Relay;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Infrastructure.Data;
using MKITS.SupportSuite.Server.Tests.TestSupport;
using SessionStatus = MKITS.SupportSuite.Server.Features.Sessions.SessionStatus;
using WireStatus = MKITS.SupportSuite.Common.Protocol.V0.SessionStatus;

namespace MKITS.SupportSuite.Server.Tests.Features;

/// <summary>End-to-end tests of the WebSocket relay: real sockets against the test server.</summary>
public sealed class RelayTests : IAsyncLifetime
{
    private readonly ManualTimeProvider _time = new(DateTimeOffset.UtcNow);
    private MkitsWebApplicationFactory _factory = default!;
    private Guid _supporterId;

    private SessionService Sessions => _factory.Services.GetRequiredService<SessionService>();

    private SessionHub Hub => _factory.Services.GetRequiredService<SessionHub>();

    public async Task InitializeAsync()
    {
        _factory = new MkitsWebApplicationFactory { ConfigureServices = services => services.AddSingleton<TimeProvider>(_time) };
        _ = _factory.Server; // start the host

        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var supporter = new MkitsUser { Id = Guid.NewGuid(), UserName = "supporter", DisplayName = "Sam Supporter" };
        Assert.True((await users.CreateAsync(supporter, "a long test passphrase")).Succeeded);
        Assert.True((await users.AddToRoleAsync(supporter, Roles.User)).Succeeded);
        _supporterId = supporter.Id;
    }

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Agent_ValidCode_IsAssigned_WithOperatorName_AndResumeToken()
    {
        var created = await CreateSessionAsync();

        var (agent, auth, assigned) = await TestPeer.ConnectAgentAsync(_factory.Server, created.Code);
        await using var _ = agent;

        Assert.True(auth.Ok);
        Assert.False(string.IsNullOrEmpty(auth.ResumeToken));
        Assert.Equal(created.SessionId.ToString(), assigned.SessionId);
        Assert.Equal(ConsentPolicy.Ask, assigned.ConsentPolicy);
        Assert.Equal("Sam Supporter", assigned.OperatorName);
        Assert.Equal(SessionStatus.WaitingForViewer, await DbStatusAsync(created.SessionId));
    }

    [Fact]
    public async Task Agent_InvalidCode_GetsError_AndIsClosed()
    {
        await using var agent = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        await agent.HelloAsync(PeerRole.Agent);
        await agent.SendAsync(new Frame { Authenticate = new Authenticate { AdHocCode = new AdHocCode { Code = "0000 0000" } } });

        await agent.ExpectErrorAndCloseAsync(ErrorCodes.CodeInvalid);
    }

    [Fact]
    public async Task Hello_WrongRole_IsRejected()
    {
        await using var peer = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        await peer.SendAsync(new Frame { Hello = new Hello { ProtocolMajor = ProtocolVersion.Major, Role = PeerRole.Viewer } });

        await peer.ExpectErrorAndCloseAsync(ErrorCodes.ProtocolMismatch);
    }

    [Fact]
    public async Task Hello_IncompatibleMajor_IsRejected()
    {
        await using var peer = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        await peer.SendAsync(new Frame { Hello = new Hello { ProtocolMajor = ProtocolVersion.Major + 1, Role = PeerRole.Agent } });

        await peer.ExpectErrorAndCloseAsync(ErrorCodes.ProtocolMismatch);
    }

    [Fact]
    public async Task Garbage_IsRejected()
    {
        await using var peer = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        await peer.SendRawAsync([0xFF, 0xFF, 0xFF]);

        await peer.ExpectErrorAndCloseAsync(ErrorCodes.ProtocolMismatch);
    }

    [Fact]
    public async Task Viewer_InvalidTicket_IsRejected()
    {
        await using var viewer = await TestPeer.ConnectAsync(_factory.Server, "/ws/viewer");
        await viewer.HelloAsync(PeerRole.Viewer);
        await viewer.SendAsync(new Frame { Authenticate = new Authenticate { ViewerTicket = new ViewerTicket { Ticket = "forged" } } });

        await viewer.ExpectErrorAndCloseAsync(ErrorCodes.AuthFailed);
    }

    [Fact]
    public async Task FullFlow_Consent_KeyExchange_AndDataRelay()
    {
        var created = await CreateSessionAsync();
        var (agent, _, _) = await TestPeer.ConnectAgentAsync(_factory.Server, created.Code);
        await using var agentHandle = agent;

        var (viewer, viewerAuth) = await TestPeer.ConnectViewerAsync(_factory.Server, await TicketAsync(created.SessionId));
        await using var viewerHandle = viewer;
        Assert.True(viewerAuth.Ok);

        var joined = (await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.ViewerJoined)).ViewerJoined;
        Assert.Equal("Sam Supporter", joined.DisplayName);
        var request = (await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.ConsentRequest)).ConsentRequest;
        Assert.Equal(viewerAuth.PeerId, request.ViewerId);
        Assert.Equal("Sam Supporter", request.SupporterDisplayName);
        Assert.Equal(SessionStatus.AwaitingConsent, await DbStatusAsync(created.SessionId));

        // Before consent nothing from the viewer reaches the agent.
        await viewer.SendAsync(DataFrame(created.SessionId, Channel.Chat, sequence: 1, viewerId: string.Empty));

        await agent.SendAsync(new Frame { ConsentResponse = new ConsentResponse { SessionId = request.SessionId, ViewerId = request.ViewerId, Granted = true } });
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);
        Assert.Equal(SessionStatus.Active, await DbStatusAsync(created.SessionId));

        // Key exchange: the server stamps the real viewer id.
        await viewer.SendAsync(new Frame { KeyExchange = new KeyExchange { ViewerId = "spoofed", EphemeralPublicKey = ByteString.CopyFrom([1, 2, 3]) } });
        var exchange = (await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.KeyExchange)).KeyExchange;
        Assert.Equal(viewerAuth.PeerId, exchange.ViewerId);

        await agent.SendAsync(new Frame { KeyExchange = new KeyExchange { ViewerId = viewerAuth.PeerId, EphemeralPublicKey = ByteString.CopyFrom([4, 5, 6]) } });
        Assert.Equal([4, 5, 6], (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.KeyExchange)).KeyExchange.EphemeralPublicKey.ToByteArray());

        // Session plane both ways.
        await viewer.SendAsync(DataFrame(created.SessionId, Channel.Chat, sequence: 2, viewerId: "spoofed"));
        var fromViewer = (await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionData)).SessionData;
        Assert.Equal(2UL, fromViewer.Sequence); // the pre-consent frame (sequence 1) was dropped
        Assert.Equal(viewerAuth.PeerId, fromViewer.ViewerId);

        await agent.SendAsync(DataFrame(created.SessionId, Channel.Video, sequence: 1, viewerId: string.Empty));
        var toViewer = (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionData)).SessionData;
        Assert.Equal((uint)Channel.Video, toViewer.Channel);
    }

    [Fact]
    public async Task ConsentDenied_EndsSession()
    {
        var created = await CreateSessionAsync();
        var (agent, _, _) = await TestPeer.ConnectAgentAsync(_factory.Server, created.Code);
        await using var agentHandle = agent;
        var (viewer, _) = await TestPeer.ConnectViewerAsync(_factory.Server, await TicketAsync(created.SessionId));
        await using var viewerHandle = viewer;

        var request = (await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.ConsentRequest)).ConsentRequest;
        await agent.SendAsync(new Frame { ConsentResponse = new ConsentResponse { SessionId = request.SessionId, ViewerId = request.ViewerId, Granted = false } });

        await viewer.ExpectErrorAndCloseAsync(ErrorCodes.ConsentDenied);
        var ended = await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        Assert.Equal(SessionEndReasons.ConsentDenied, ended.SessionState.EndReason);
        Assert.Equal(SessionStatus.Ended, await DbStatusAsync(created.SessionId));
    }

    [Fact]
    public async Task ConsentTimeout_EndsSession()
    {
        var created = await CreateSessionAsync();
        var (agent, _, _) = await TestPeer.ConnectAgentAsync(_factory.Server, created.Code);
        await using var agentHandle = agent;
        var (viewer, _) = await TestPeer.ConnectViewerAsync(_factory.Server, await TicketAsync(created.SessionId));
        await using var viewerHandle = viewer;
        await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.ConsentRequest);

        _time.Advance(TimeSpan.FromMinutes(2));
        await Hub.TickAsync(CancellationToken.None);

        await viewer.ExpectErrorAndCloseAsync(ErrorCodes.ConsentDenied);
        Assert.Equal(SessionEndReasons.ConsentTimeout, await DbEndReasonAsync(created.SessionId));
    }

    [Fact]
    public async Task SupporterEndsSession_ClosesAllConnections()
    {
        var (agent, viewer, created) = await StartActiveSessionAsync();
        await using var agentHandle = agent;
        await using var viewerHandle = viewer;

        Assert.True(await Sessions.EndAsync(created.SessionId, _supporterId, actorIsAdmin: false));

        var ended = await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        Assert.Equal(SessionEndReasons.EndedBySupporter, ended.SessionState.EndReason);
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        Assert.Null(await agent.ReceiveAsync());
    }

    [Fact]
    public async Task CustomerEndsSession_IsRecorded()
    {
        var (agent, viewer, created) = await StartActiveSessionAsync();
        await using var agentHandle = agent;
        await using var viewerHandle = viewer;

        await agent.SendAsync(new Frame { EndSession = new EndSession { SessionId = created.SessionId.ToString() } });

        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        Assert.Equal(SessionEndReasons.EndedByCustomer, await DbEndReasonAsync(created.SessionId));
    }

    [Fact]
    public async Task AgentConnectionLost_Suspends_AndResumeRestoresSession()
    {
        var created = await CreateSessionAsync();
        var (agent, auth, _) = await TestPeer.ConnectAgentAsync(_factory.Server, created.Code);
        var (viewer, viewerAuth) = await TestPeer.ConnectViewerAsync(_factory.Server, await TicketAsync(created.SessionId));
        await using var viewerHandle = viewer;
        await GrantAsync(agent, viewer);

        agent.Abort();
        await agent.DisposeAsync();
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Suspended);

        // A wrong token is refused …
        await using (var intruder = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent"))
        {
            await intruder.HelloAsync(PeerRole.Agent);
            await intruder.SendAsync(new Frame { Authenticate = new Authenticate { Resume = new Resume { SessionId = created.SessionId.ToString(), Token = "wrong" } } });
            await intruder.ExpectErrorAndCloseAsync(ErrorCodes.AuthFailed);
        }

        // … the right one restores the session with the already granted viewer.
        await using var resumed = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        await resumed.HelloAsync(PeerRole.Agent);
        await resumed.SendAsync(new Frame { Authenticate = new Authenticate { Resume = new Resume { SessionId = created.SessionId.ToString(), Token = auth.ResumeToken } } });
        var resumedAuth = (await resumed.ExpectAsync(Frame.BodyOneofCase.AuthResult)).AuthResult;
        Assert.NotEqual(auth.ResumeToken, resumedAuth.ResumeToken);

        var rejoined = (await resumed.ExpectEventuallyAsync(Frame.BodyOneofCase.ViewerJoined)).ViewerJoined;
        Assert.Equal(viewerAuth.PeerId, rejoined.ViewerId);
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);
    }

    [Fact]
    public async Task AgentConnectionLost_GracePeriodExpires_EndsSession()
    {
        var (agent, viewer, created) = await StartActiveSessionAsync();
        await using var viewerHandle = viewer;

        agent.Abort();
        await agent.DisposeAsync();
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Suspended);

        _time.Advance(TimeSpan.FromSeconds(61));
        await Hub.TickAsync(CancellationToken.None);

        var ended = await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        Assert.Equal(SessionEndReasons.ConnectionLost, ended.SessionState.EndReason);
        Assert.Equal(SessionEndReasons.ConnectionLost, await DbEndReasonAsync(created.SessionId));
    }

    [Fact]
    public async Task ViewerLeaves_SessionWaitsForViewerAgain()
    {
        var (agent, viewer, created) = await StartActiveSessionAsync();
        await using var agentHandle = agent;

        await viewer.DisposeAsync();

        await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.ViewerLeft);
        await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.WaitingForViewer);
        Assert.Equal(SessionStatus.WaitingForViewer, await DbStatusAsync(created.SessionId));
    }

    [Fact]
    public async Task Heartbeat_SilentPeer_IsDisconnected()
    {
        var created = await CreateSessionAsync();
        var (agent, _, _) = await TestPeer.ConnectAgentAsync(_factory.Server, created.Code);
        await using var agentHandle = agent;

        _time.Advance(SessionHub.HeartbeatTimeout + TimeSpan.FromSeconds(1));
        await Hub.TickAsync(CancellationToken.None);

        Assert.Null(await agent.ReceiveAsync());
    }

    [Fact]
    public async Task NotAWebSocket_Returns400()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/ws/agent", UriKind.Relative));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<(TestPeer Agent, TestPeer Viewer, CreatedSession Created)> StartActiveSessionAsync()
    {
        var created = await CreateSessionAsync();
        var (agent, _, _) = await TestPeer.ConnectAgentAsync(_factory.Server, created.Code);
        var (viewer, _) = await TestPeer.ConnectViewerAsync(_factory.Server, await TicketAsync(created.SessionId));
        await GrantAsync(agent, viewer);
        return (agent, viewer, created);
    }

    private static async Task GrantAsync(TestPeer agent, TestPeer viewer)
    {
        var request = (await agent.ExpectEventuallyAsync(Frame.BodyOneofCase.ConsentRequest)).ConsentRequest;
        await agent.SendAsync(new Frame { ConsentResponse = new ConsentResponse { SessionId = request.SessionId, ViewerId = request.ViewerId, Granted = true } });
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);
    }

    private async Task<CreatedSession> CreateSessionAsync()
    {
        var result = await Sessions.CreateAdHocAsync(_supporterId, "relay test");
        return Assert.IsType<CreatedSession>(result.Session);
    }

    private async Task<string> TicketAsync(Guid sessionId)
    {
        var ticket = await Sessions.IssueViewerTicketAsync(sessionId, _supporterId, userIsAdmin: false);
        return Assert.IsType<string>(ticket);
    }

    private async Task<SessionStatus> DbStatusAsync(Guid sessionId)
    {
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<MkitsDbContext>>().CreateDbContextAsync();
        return await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.Status).SingleAsync();
    }

    private async Task<string?> DbEndReasonAsync(Guid sessionId)
    {
        await using var db = await _factory.Services.GetRequiredService<IDbContextFactory<MkitsDbContext>>().CreateDbContextAsync();
        return await db.Sessions.Where(s => s.Id == sessionId).Select(s => s.EndReason).SingleAsync();
    }

    private static Frame DataFrame(Guid sessionId, Channel channel, ulong sequence, string viewerId) => new()
    {
        SessionData = new SessionData
        {
            SessionId = sessionId.ToString(),
            Channel = (uint)channel,
            Sequence = sequence,
            ViewerId = viewerId,
            Ciphertext = ByteString.CopyFrom(new byte[64]),
        },
    };
}
