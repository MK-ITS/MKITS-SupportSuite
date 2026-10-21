// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Agent.Core.Devices;
using MKITS.SupportSuite.Agent.Core.Sessions;
using MKITS.SupportSuite.Common.Crypto;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Relay;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Tests.TestSupport;
using EchoChatHandler = MKITS.SupportSuite.Server.Tests.Features.AgentEndToEndTests.EchoChatHandler;
using RecordingUi = MKITS.SupportSuite.Server.Tests.Features.AgentEndToEndTests.RecordingUi;
using WireStatus = MKITS.SupportSuite.Common.Protocol.V0.SessionStatus;

namespace MKITS.SupportSuite.Server.Tests.Features;

/// <summary>The real permanent agent (<see cref="DeviceAgent"/>) against the real relay.</summary>
public sealed class DeviceAgentEndToEndTests : IAsyncLifetime, IDisposable
{
    private static readonly TimeSpan Timeout = AgentEndToEndTests.Timeout;
    private static readonly string[] ViewOnly = ["view"];

    private readonly MkitsWebApplicationFactory _factory = new();
    private readonly CancellationTokenSource _stop = new();
    private Guid _supporterId;

    private DeviceService Devices => _factory.Services.GetRequiredService<DeviceService>();

    private SessionService Sessions => _factory.Services.GetRequiredService<SessionService>();

    private SessionHub Hub => _factory.Services.GetRequiredService<SessionHub>();

    public async Task InitializeAsync()
    {
        _ = _factory.Server;
        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var supporter = new MkitsUser { Id = Guid.NewGuid(), UserName = "sam", Email = "sam@example.test", DisplayName = "Sam Supporter" };
        Assert.True((await users.CreateAsync(supporter, "a long test passphrase")).Succeeded);
        _supporterId = supporter.Id;
    }

    public async Task DisposeAsync() => await _stop.CancelAsync();

    public void Dispose()
    {
        _stop.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Enroll_GoOnline_Session_SignedKeyExchange_EncryptedChat_ThenIdleAgain()
    {
        var groupId = (await Devices.CreateGroupAsync(_supporterId, "Customer A", null, DeviceConsentPolicy.Ask, false)).Id!.Value;
        Assert.Null(await Devices.SetPermissionAsync(_supporterId, groupId, _supporterId, DevicePermissions.View));
        var token = (await Devices.CreateTokenAsync(_supporterId, groupId, null, TimeSpan.FromDays(1), 1))!;

        var store = new MemoryIdentityStore();
        var ui = new RecordingUi(grant: true);
        var chat = new EchoChatHandler();
        await using var agent = new DeviceAgent(Options(token.Token), new TestServerConnector(_factory.Server), store, ui, [chat]);
        var run = agent.RunAsync(_stop.Token);

        // enrolled, identity stored, online
        await ui.WaitForStatusAsync(AgentStatus.Idle);
        var deviceId = Assert.IsType<Guid>(agent.DeviceId);
        Assert.NotNull(store.Saved);
        Assert.True(Hub.IsDeviceOnline(deviceId));

        // a supporter opens the device
        var (sessionId, error) = await _factory.Services.GetRequiredService<DeviceSessionLauncher>().OpenAsync(_supporterId, isAdmin: false, deviceId);
        Assert.Null(error);
        await ui.WaitForStatusAsync(AgentStatus.WaitingForSupporter);
        Assert.Equal(ConsentPolicy.Ask, Assert.Single(ui.Policies));

        var ticket = await Sessions.IssueViewerTicketAsync(sessionId!.Value, _supporterId, userIsAdmin: false);
        var (viewer, viewerAuth) = await TestPeer.ConnectViewerAsync(_factory.Server, ticket!);
        await using var viewerHandle = viewer;
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);
        Assert.Equal(ViewOnly, Assert.Single(ui.Prompts).Permissions);

        // key exchange: the agent signs its ephemeral key with the enrolled device key
        using var viewerKey = new E2EKeyPair();
        await viewer.SendAsync(new Frame { KeyExchange = new KeyExchange { EphemeralPublicKey = ByteString.CopyFrom(viewerKey.PublicKey) } });
        var answer = (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.KeyExchange)).KeyExchange;
        using (var deviceKey = ECDsa.Create())
        {
            deviceKey.ImportSubjectPublicKeyInfo(await Devices.GetPublicKeyAsync(deviceId), out _);
            Assert.True(deviceKey.VerifyData(
                answer.EphemeralPublicKey.ToByteArray(), answer.DeviceSignature.ToByteArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        var wireSessionId = sessionId.Value.ToString();
        using var cipher = SessionCrypto.Establish(E2ERole.Viewer, viewerKey, wireSessionId, answer.EphemeralPublicKey.ToByteArray(), viewerKey.PublicKey);
        var payload = new SessionPayload { ChatMessage = new ChatMessage { Text = "hello device" } };
        var (sequence, ciphertext) = cipher.Encrypt((uint)Channel.Chat, payload.ToByteArray());
        await viewer.SendAsync(new Frame
        {
            SessionData = new SessionData { SessionId = wireSessionId, Channel = (uint)Channel.Chat, Sequence = sequence, Ciphertext = ByteString.CopyFrom(ciphertext) },
        });
        Assert.Equal("hello device", await chat.Received.Task.WaitAsync(Timeout));
        var echo = (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionData)).SessionData;
        Assert.Equal(viewerAuth.PeerId, echo.ViewerId);
        Assert.NotNull(cipher.Decrypt(echo.Channel, echo.Sequence, echo.Ciphertext.Span));

        // the supporter ends the session: the device stays online and idle
        await viewer.SendAsync(new Frame { EndSession = new EndSession { SessionId = wireSessionId, Reason = "done" } });
        await ui.WaitForEventAfterAsync(nameof(AgentStatus.Active), nameof(AgentStatus.Idle));
        Assert.Null(agent.SessionId);
        Assert.True(Hub.IsDeviceOnline(deviceId));
        Assert.False(run.IsCompleted);

        await _stop.CancelAsync();
        Assert.Equal(DeviceAgentStopReason.Cancelled, await run.WaitAsync(Timeout));
    }

    [Fact]
    public async Task StoredIdentity_IsUsed_WithoutToken()
    {
        var groupId = (await Devices.CreateGroupAsync(_supporterId, "Customer B", null, DeviceConsentPolicy.Silent, false)).Id!.Value;
        var token = (await Devices.CreateTokenAsync(_supporterId, groupId, null, TimeSpan.FromDays(1), 1))!;
        var store = new MemoryIdentityStore();

        // first start: enrolls with the token
        using (var stopFirst = new CancellationTokenSource())
        {
            var ui = new RecordingUi(true);
            await using var first = new DeviceAgent(Options(token.Token), new TestServerConnector(_factory.Server), store, ui, []);
            var run = first.RunAsync(stopFirst.Token);
            await ui.WaitForStatusAsync(AgentStatus.Idle);
            await stopFirst.CancelAsync();
            await run.WaitAsync(Timeout);
        }

        // second start: no token needed, same device id (the single-use token is consumed already)
        var secondUi = new RecordingUi(true);
        await using var second = new DeviceAgent(Options(null), new TestServerConnector(_factory.Server), store, secondUi, []);
        var secondRun = second.RunAsync(_stop.Token);
        await secondUi.WaitForStatusAsync(AgentStatus.Idle);
        Assert.Equal(store.Saved!.Value.DeviceId, second.DeviceId);
        Assert.True(Hub.IsDeviceOnline(store.Saved.Value.DeviceId));
        await _stop.CancelAsync();
        await secondRun.WaitAsync(Timeout);
    }

    [Fact]
    public async Task NotEnrolled_WithoutToken_Stops()
    {
        await using var agent = new DeviceAgent(Options(null), new TestServerConnector(_factory.Server), new MemoryIdentityStore(), new RecordingUi(true), []);

        Assert.Equal(DeviceAgentStopReason.NotEnrolled, await agent.RunAsync(_stop.Token).WaitAsync(Timeout));
    }

    [Fact]
    public async Task InvalidToken_StopsWithEnrollmentRejected()
    {
        await using var agent = new DeviceAgent(Options("not-a-token"), new TestServerConnector(_factory.Server), new MemoryIdentityStore(), new RecordingUi(true), []);

        Assert.Equal(DeviceAgentStopReason.EnrollmentRejected, await agent.RunAsync(_stop.Token).WaitAsync(Timeout));
    }

    private DeviceAgentOptions Options(string? token) => new()
    {
        ServerUri = _factory.Server.BaseAddress,
        EnrollmentToken = token,
        Machine = new MachineInfo { MachineName = "SERVER-01", OsName = "Windows Server", OsVersion = "2025" },
        Platform = "windows",
        MinReconnectDelay = TimeSpan.FromMilliseconds(100),
    };

    /// <summary>Keeps the identity in memory (as exported bytes, like a real store).</summary>
    private sealed class MemoryIdentityStore : IDeviceIdentityStore
    {
        public (Guid DeviceId, byte[] Key)? Saved { get; private set; }

        public DeviceIdentity? Load() => Saved is { } saved ? DeviceIdentity.Import(saved.DeviceId, saved.Key) : null;

        public void Save(DeviceIdentity identity) => Saved = (identity.DeviceId, identity.ExportPrivateKey());

        public void Delete() => Saved = null;
    }
}
