// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Crypto;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Tests.TestSupport;
using WireStatus = MKITS.SupportSuite.Common.Protocol.V0.SessionStatus;

namespace MKITS.SupportSuite.Server.Tests.Features;

/// <summary>The real agent core against the real relay; the viewer side is a test peer using the shared crypto.</summary>
public sealed class AgentEndToEndTests : IAsyncLifetime, IDisposable
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly MkitsWebApplicationFactory _factory = new();
    private Guid _supporterId;

    private SessionService Sessions => _factory.Services.GetRequiredService<SessionService>();

    public async Task InitializeAsync()
    {
        _ = _factory.Server;
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var supporter = new MkitsUser { Id = Guid.NewGuid(), UserName = "sam", Email = "sam@example.test", DisplayName = "Sam Supporter" };
        Assert.True((await users.CreateAsync(supporter, "a long test passphrase")).Succeeded);
        _supporterId = supporter.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task AdHocSession_Consent_KeyExchange_EncryptedChatBothWays_AndCustomerEnds()
    {
        var created = (await Sessions.CreateAdHocAsync(_supporterId, null)).Session!;
        var ui = new RecordingUi(grant: true);
        var chat = new EchoChatHandler();
        await using var agent = new AgentSession(Options(created.Code), new TestServerConnector(_factory.Server), ui, [chat]);
        var run = agent.RunAsync(CancellationToken.None);

        await ui.WaitForStatusAsync(AgentStatus.WaitingForSupporter);

        var ticket = await Sessions.IssueViewerTicketAsync(created.SessionId, _supporterId, userIsAdmin: false);
        var (viewer, viewerAuth) = await TestPeer.ConnectViewerAsync(_factory.Server, ticket!);
        await using var viewerHandle = viewer;
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);

        // Consent dialog showed the right information.
        var prompt = Assert.Single(ui.Prompts);
        Assert.Equal("Sam Supporter", prompt.SupporterDisplayName);
        Assert.Equal("Sam Supporter", prompt.OperatorName);

        // Key exchange: viewer starts, agent answers.
        using var viewerKey = new E2EKeyPair();
        await viewer.SendAsync(new Frame { KeyExchange = new KeyExchange { EphemeralPublicKey = ByteString.CopyFrom(viewerKey.PublicKey) } });
        var answer = (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.KeyExchange)).KeyExchange;
        var agentKey = answer.EphemeralPublicKey.ToByteArray();
        var sessionId = created.SessionId.ToString();
        using var cipher = SessionCrypto.Establish(E2ERole.Viewer, viewerKey, sessionId, agentKey, viewerKey.PublicKey);

        Assert.Equal(SessionCrypto.ComputeSecurityCode(sessionId, agentKey, viewerKey.PublicKey), await ui.SecurityCode.Task.WaitAsync(Timeout));

        // Viewer → agent (encrypted chat), agent echoes back.
        var payload = new SessionPayload { ChatMessage = new ChatMessage { Author = "Sam", Text = "Hello from support" } };
        var (sequence, ciphertext) = cipher.Encrypt((uint)Channel.Chat, payload.ToByteArray());
        await viewer.SendAsync(new Frame
        {
            SessionData = new SessionData { SessionId = sessionId, Channel = (uint)Channel.Chat, Sequence = sequence, Ciphertext = ByteString.CopyFrom(ciphertext) },
        });

        Assert.Equal("Hello from support", await chat.Received.Task.WaitAsync(Timeout));

        var echo = (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionData)).SessionData;
        Assert.Equal(viewerAuth.PeerId, echo.ViewerId);
        var decrypted = cipher.Decrypt(echo.Channel, echo.Sequence, echo.Ciphertext.Span);
        Assert.NotNull(decrypted);
        Assert.Equal("echo: Hello from support", SessionPayload.Parser.ParseFrom(decrypted).ChatMessage.Text);

        // The customer ends the session.
        await agent.EndAsync();
        var result = await run.WaitAsync(Timeout);
        Assert.True(result.Succeeded);
        var ended = await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        Assert.Equal(SessionEndReasons.EndedByCustomer, ended.SessionState.EndReason);
    }

    [Fact]
    public async Task ConnectionLoss_AgentResumes_AndNewKeyExchangeRestoresChat()
    {
        var created = (await Sessions.CreateAdHocAsync(_supporterId, null)).Session!;
        var ui = new RecordingUi(grant: true);
        var chat = new EchoChatHandler();
        var connector = new TestServerConnector(_factory.Server);
        await using var agent = new AgentSession(Options(created.Code), connector, ui, [chat]);
        var run = agent.RunAsync(CancellationToken.None);
        await ui.WaitForStatusAsync(AgentStatus.WaitingForSupporter);

        var ticket = await Sessions.IssueViewerTicketAsync(created.SessionId, _supporterId, userIsAdmin: false);
        var (viewer, _) = await TestPeer.ConnectViewerAsync(_factory.Server, ticket!);
        await using var viewerHandle = viewer;
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);

        connector.LastSocket!.Abort();

        // Depending on who notices the loss first, the server suspends the session or replaces the connection directly.
        // Wait until the agent knows the supporter again (ViewerJoined after the resume) before starting the key exchange.
        await ui.WaitForEventAfterAsync(nameof(AgentStatus.Reconnecting), "supporters=1");
        Assert.Equal(2, connector.Connections);
        Assert.Single(ui.Prompts); // consent survives the reconnect

        // The old keys are gone; a new key exchange makes the session plane work again.
        var sessionId = created.SessionId.ToString();
        using var viewerKey = new E2EKeyPair();
        await viewer.SendAsync(new Frame { KeyExchange = new KeyExchange { EphemeralPublicKey = ByteString.CopyFrom(viewerKey.PublicKey) } });
        var answer = (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.KeyExchange)).KeyExchange;
        using var cipher = SessionCrypto.Establish(E2ERole.Viewer, viewerKey, sessionId, answer.EphemeralPublicKey.ToByteArray(), viewerKey.PublicKey);

        var payload = new SessionPayload { ChatMessage = new ChatMessage { Text = "still there?" } };
        var (sequence, ciphertext) = cipher.Encrypt((uint)Channel.Chat, payload.ToByteArray());
        await viewer.SendAsync(new Frame
        {
            SessionData = new SessionData { SessionId = sessionId, Channel = (uint)Channel.Chat, Sequence = sequence, Ciphertext = ByteString.CopyFrom(ciphertext) },
        });
        Assert.Equal("still there?", await chat.Received.Task.WaitAsync(Timeout));

        await agent.EndAsync();
        Assert.True((await run.WaitAsync(Timeout)).Succeeded);
    }

    [Fact]
    public async Task InvalidCode_ReturnsCodeInvalid()
    {
        var ui = new RecordingUi(grant: true);
        await using var agent = new AgentSession(Options("0000 0000"), new TestServerConnector(_factory.Server), ui, []);

        var result = await agent.RunAsync(CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(ErrorCodes.CodeInvalid, result.ErrorCode);
        Assert.Equal(AgentStatus.Ended, ui.Statuses.Last());
    }

    [Fact]
    public async Task ConsentDenied_EndsSession()
    {
        var created = (await Sessions.CreateAdHocAsync(_supporterId, null)).Session!;
        var ui = new RecordingUi(grant: false);
        await using var agent = new AgentSession(Options(created.Code), new TestServerConnector(_factory.Server), ui, []);
        var run = agent.RunAsync(CancellationToken.None);
        await ui.WaitForStatusAsync(AgentStatus.WaitingForSupporter);

        var ticket = await Sessions.IssueViewerTicketAsync(created.SessionId, _supporterId, userIsAdmin: false);
        var (viewer, _) = await TestPeer.ConnectViewerAsync(_factory.Server, ticket!);
        await using var viewerHandle = viewer;

        var result = await run.WaitAsync(Timeout);
        Assert.Equal(SessionEndReasons.ConsentDenied, result.EndReason);
    }

    [Fact]
    public void Options_FromFileNameTarget_UseTls()
    {
        var options = AgentSessionOptions.FromTarget(
            new Common.Sessions.AdHocClientTarget("support.example.com", 8443, "12345678"), new MachineInfo(), "windows");

        Assert.Equal(new Uri("wss://support.example.com:8443/ws/agent"), options.AgentEndpoint);
        Assert.Equal(new Uri("wss://support.example.com/ws/agent"),
            AgentSessionOptions.FromTarget(new Common.Sessions.AdHocClientTarget("support.example.com", null, "12345678"), new MachineInfo(), "windows").AgentEndpoint);
    }

    private AgentSessionOptions Options(string code) => new()
    {
        ServerUri = _factory.Server.BaseAddress,
        Code = code,
        Machine = new MachineInfo { MachineName = "CUSTOMER-PC", OsName = "Windows", OsVersion = "11" },
        Platform = "windows",
    };

    internal sealed class RecordingUi(bool grant) : IAgentUserInterface
    {
        private readonly ConcurrentDictionary<AgentStatus, TaskCompletionSource> _reached = new();

        public ConcurrentQueue<ConsentPrompt> Prompts { get; } = new();

        public ConcurrentQueue<AgentStatus> Statuses { get; } = new();

        public TaskCompletionSource<string> SecurityCode { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> RequestConsentAsync(ConsentPrompt prompt, CancellationToken cancellationToken)
        {
            Prompts.Enqueue(prompt);
            return Task.FromResult(grant);
        }

        /// <summary>Statuses and supporter changes in order, e.g. <c>Reconnecting</c>, <c>supporters=1</c>.</summary>
        public ConcurrentQueue<string> Events { get; } = new();

        public void OnStatusChanged(AgentStatus status, string? detail)
        {
            Statuses.Enqueue(status);
            Events.Enqueue(status.ToString());
            Reached(status).TrySetResult();
        }

        public void OnSupportersChanged(IReadOnlyList<string> displayNames) => Events.Enqueue($"supporters={displayNames.Count}");

        public void OnSecurityCode(string supporterDisplayName, string securityCode) => SecurityCode.TrySetResult(securityCode);

        public ConcurrentQueue<ConsentPolicy> Policies { get; } = new();

        public void OnSessionAssigned(string operatorName, ConsentPolicy policy) => Policies.Enqueue(policy);

        public Task WaitForStatusAsync(AgentStatus status) => Reached(status).Task.WaitAsync(Timeout);

        /// <summary>Waits until event <paramref name="then"/> was recorded after event <paramref name="first"/>.</summary>
        public async Task WaitForEventAfterAsync(string first, string then)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                var events = Events.ToArray();
                var index = Array.IndexOf(events, first);
                if (index >= 0 && Array.IndexOf(events, then, index + 1) > index)
                {
                    return;
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"{then} after {first} not recorded: {string.Join(", ", Events)}");
        }

        private TaskCompletionSource Reached(AgentStatus status) =>
            _reached.GetOrAdd(status, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    internal sealed class EchoChatHandler : ISessionChannelHandler
    {
        public TaskCompletionSource<string> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HandleAsync(ISessionContext context, string viewerId, SessionPayload payload, CancellationToken cancellationToken)
        {
            if (payload.BodyCase != SessionPayload.BodyOneofCase.ChatMessage)
            {
                return;
            }

            Received.TrySetResult(payload.ChatMessage.Text);
            await context.SendAsync(viewerId, new SessionPayload { ChatMessage = new ChatMessage { Author = "Customer", Text = "echo: " + payload.ChatMessage.Text } }, cancellationToken);
        }
    }
}
