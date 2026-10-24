// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MKITS.SupportSuite.Agent.Core.Bridge;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Protocol.V0;

namespace MKITS.SupportSuite.Agent.Core.Tests;

/// <summary>Service ↔ user-session split over a real (loopback TCP) stream.</summary>
public sealed class UserSessionBridgeTests : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] ViewAndControl = ["view", "control"];
    private static readonly string[] ViewOnly = ["view"];
    private static readonly string[] Sam = ["Sam"];

    private readonly LoopbackConnector _connector = new();
    private readonly RecordingUi _fallback = new(answer: false);
    private readonly RecordingUi _userUi = new(answer: true);
    private readonly EchoChat _chat = new();
    private readonly RecordingContext _context = new();
    private readonly UserSessionBridge _bridge;
    private BridgeClient? _client;
    private Task? _clientRun;

    public UserSessionBridgeTests() => _bridge = new UserSessionBridge(_connector, _fallback);

    public async ValueTask DisposeAsync()
    {
        await _bridge.DisposeAsync();
        if (_clientRun is not null)
        {
            await _clientRun.WaitAsync(Timeout);
        }

        _client?.Dispose();
        await _connector.DisposeAsync();
    }

    [Fact]
    public async Task Session_ForwardsUserChannels_BothWays_AndKeepsVideoInTheService()
    {
        await StartSessionAsync();

        await _bridge.OnViewerReadyAsync(_context, "v1", CancellationToken.None);
        await Eventually(() => _client!.ReadyViewerIds.Contains("v1"));

        // video stays in the service
        await _bridge.HandleAsync(_context, "v1", new SessionPayload { VideoFrame = new VideoFrame() }, CancellationToken.None);

        // chat goes to the user session; its answer comes back through the service's context
        await _bridge.HandleAsync(_context, "v1", new SessionPayload { ChatMessage = new ChatMessage { Text = "hello" } }, CancellationToken.None);
        Assert.Equal("hello", await _chat.Received.Task.WaitAsync(Timeout));
        await Eventually(() => !_context.Sent.IsEmpty);
        var (viewerId, payload) = Assert.Single(_context.Sent);
        Assert.Equal("v1", viewerId);
        Assert.Equal("echo: hello", payload.ChatMessage.Text);
        Assert.Equal(1, _chat.Count);
    }

    [Fact]
    public async Task Consent_IsAskedInTheUserSession_StatusAndSupportersArrive()
    {
        await StartSessionAsync();
        _bridge.OnStatusChanged(AgentStatus.AwaitingConsent, null);
        _bridge.OnSupportersChanged(Sam);

        var granted = await _bridge.RequestConsentAsync(new ConsentPrompt("support.example.com", "ACME", "Sam", ViewAndControl), CancellationToken.None)
            .WaitAsync(Timeout);

        Assert.True(granted);
        var prompt = Assert.Single(_userUi.Prompts);
        Assert.Equal("Sam", prompt.SupporterDisplayName);
        Assert.Equal(ViewAndControl, prompt.Permissions);
        Assert.Empty(_fallback.Prompts);
        await Eventually(() => _userUi.Statuses.Contains(AgentStatus.AwaitingConsent) && _userUi.Supporters.Contains("Sam"));
        Assert.Equal((ConsentPolicy.Notify, "ACME"), _userUi.Assigned);
    }

    [Fact]
    public async Task EndSession_FromTheUser_RaisesTheEvent_AndIdleStopsTheProcess()
    {
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _bridge.EndSessionRequested += (_, _) => requested.TrySetResult();
        await StartSessionAsync();

        await _client!.EndSessionAsync(CancellationToken.None);
        await requested.Task.WaitAsync(Timeout);

        _bridge.OnStatusChanged(AgentStatus.Idle, null);
        await _clientRun!.WaitAsync(Timeout); // the service closed the connection
        Assert.False(_bridge.IsConnected);
        Assert.Contains(AgentStatus.Idle, _fallback.Statuses);
    }

    [Fact]
    public async Task NobodyLoggedOn_ConsentGoesToTheFallback()
    {
        _connector.Available = false;
        _bridge.OnSessionAssigned("ACME", ConsentPolicy.Ask);

        var granted = await _bridge.RequestConsentAsync(new ConsentPrompt("s", "ACME", "Sam", ViewOnly), CancellationToken.None).WaitAsync(Timeout);

        Assert.False(granted);
        Assert.Single(_fallback.Prompts);
        Assert.False(_bridge.IsConnected);
    }

    [Fact]
    public async Task UserSession_CannotSendVideo_OrUnknownMessages()
    {
        await StartSessionAsync();
        await _bridge.OnViewerReadyAsync(_context, "v1", CancellationToken.None);

        // a hostile user-session process tries to inject video: ignored by the service
        await _client!.SendAsync("v1", new SessionPayload { VideoFrame = new VideoFrame() }, CancellationToken.None);
        await _client.SendAsync("v1", new SessionPayload { ChatMessage = new ChatMessage { Text = "ok" } }, CancellationToken.None);
        await Eventually(() => !_context.Sent.IsEmpty);
        Assert.Equal(SessionPayload.BodyOneofCase.ChatMessage, Assert.Single(_context.Sent).Payload.BodyCase);

        // an unexpected message type drops the connection
        await BridgeProtocol.WriteAsync(_connector.ClientStream!, BridgeProtocol.Build(BridgeMessageType.ViewerReady, w => w.Write("x")), CancellationToken.None);
        await Eventually(() => !_bridge.IsConnected);
    }

    [Fact]
    public async Task MalformedStringLength_DropsTheUserSession()
    {
        await StartSessionAsync();

        // Payload message whose viewer-id string has an invalid 7-bit length prefix (5 continuation bytes).
        await BridgeProtocol.WriteAsync(_connector.ClientStream!, [(byte)BridgeMessageType.Payload, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01], CancellationToken.None);

        await Eventually(() => !_bridge.IsConnected);
    }

    private async Task StartSessionAsync()
    {
        _bridge.OnSessionAssigned("ACME", ConsentPolicy.Notify);
        var clientStream = await _connector.ClientStreamReady.Task.WaitAsync(Timeout);
        _client = new BridgeClient(clientStream, _userUi, [_chat]);
        _clientRun = _client.RunAsync(CancellationToken.None);
        await Eventually(() => _bridge.IsConnected && _userUi.Assigned is not null);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(20);
        }
    }

    /// <summary>"Starts" the user-session process by opening a loopback TCP connection.</summary>
    private sealed class LoopbackConnector : IUserSessionConnector, IAsyncDisposable
    {
        private readonly List<IDisposable> _resources = [];

        public bool Available { get; set; } = true;

        public TaskCompletionSource<Stream> ClientStreamReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream? ClientStream { get; private set; }

        public async Task<IUserSessionConnection?> ConnectAsync(CancellationToken cancellationToken)
        {
            if (!Available)
            {
                return null;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancellationToken);
            var server = await listener.AcceptTcpClientAsync(cancellationToken);
            listener.Stop();
            _resources.Add(client);
            ClientStream = client.GetStream();
            ClientStreamReady.TrySetResult(ClientStream);
            return new Connection(server);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var resource in _resources)
            {
                resource.Dispose();
            }

            return ValueTask.CompletedTask;
        }

        private sealed class Connection(TcpClient server) : IUserSessionConnection
        {
            public Stream Stream { get; } = server.GetStream();

            public ValueTask DisposeAsync()
            {
                server.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingUi(bool answer) : IAgentUserInterface
    {
        public ConcurrentQueue<ConsentPrompt> Prompts { get; } = new();

        public ConcurrentQueue<AgentStatus> Statuses { get; } = new();

        public ConcurrentQueue<string> Supporters { get; } = new();

        public (ConsentPolicy Policy, string Operator)? Assigned { get; private set; }

        public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            return Task.FromResult(answer);
        }

        public void OnStatusChanged(AgentStatus status, string? detail) => Statuses.Enqueue(status);

        public void OnSupportersChanged(IReadOnlyList<string> displayNames)
        {
            foreach (var name in displayNames)
            {
                Supporters.Enqueue(name);
            }
        }

        public void OnSecurityCode(string supporterDisplayName, string securityCode)
        {
        }

        public void OnSessionAssigned(string operatorName, ConsentPolicy policy) => Assigned = (policy, operatorName);
    }

    private sealed class EchoChat : ISessionChannelHandler
    {
        private int _count;

        public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => Volatile.Read(ref _count);

        public async Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _count);
            if (payload.BodyCase == SessionPayload.BodyOneofCase.ChatMessage)
            {
                Received.TrySetResult(payload.ChatMessage.Text);
                await context.SendAsync(viewerId, new SessionPayload { ChatMessage = new ChatMessage { Text = "echo: " + payload.ChatMessage.Text } }, cancellationToken);
            }
        }
    }
}
