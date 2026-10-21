// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.Identity;
using MKITS.SupportSuite.Server.Features.Relay;
using MKITS.SupportSuite.Server.Features.Sessions;
using MKITS.SupportSuite.Server.Infrastructure.Data;
using MKITS.SupportSuite.Server.Tests.TestSupport;
using SessionStatus = MKITS.SupportSuite.Server.Features.Sessions.SessionStatus;
using WireStatus = MKITS.SupportSuite.Common.Protocol.V0.SessionStatus;

namespace MKITS.SupportSuite.Server.Tests.Features;

/// <summary>Permanent devices on the relay: enrollment, key authentication, presence, policies and permissions.</summary>
public sealed class DeviceRelayTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] ViewAndControl = ["view", "control"];

    private readonly ECDsa _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private MkitsWebApplicationFactory _factory = default!;
    private Guid _supporterId;

    private DeviceService Devices => _factory.Services.GetRequiredService<DeviceService>();

    private SessionService Sessions => _factory.Services.GetRequiredService<SessionService>();

    private DeviceSessionLauncher Launcher => _factory.Services.GetRequiredService<DeviceSessionLauncher>();

    private SessionHub Hub => _factory.Services.GetRequiredService<SessionHub>();

    public async Task InitializeAsync()
    {
        _factory = new MkitsWebApplicationFactory();
        _ = _factory.Server;

        await using var scope = _factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<MkitsUser>>();
        var supporter = new MkitsUser { Id = Guid.NewGuid(), UserName = "supporter", Email = "supporter@example.test", DisplayName = "Sam Supporter" };
        Assert.True((await users.CreateAsync(supporter, "a long test passphrase")).Succeeded);
        Assert.True((await users.AddToRoleAsync(supporter, Roles.User)).Succeeded);
        _supporterId = supporter.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _deviceKey.Dispose();
        _factory?.Dispose();
    }

    [Fact]
    public async Task Enrollment_ReturnsDeviceId_AndCloses_ThenSignatureBringsDeviceOnline()
    {
        var groupId = await CreateGroupAsync(DeviceConsentPolicy.Ask);

        var deviceId = await EnrollAsync(groupId);
        Assert.False(Hub.IsDeviceOnline(deviceId));

        var (device, first) = await ConnectDeviceAsync(deviceId);
        await using var _ = device;

        Assert.Equal(Frame.BodyOneofCase.AuthResult, first?.BodyCase);
        Assert.True(first!.AuthResult.Ok);
        Assert.True(Hub.IsDeviceOnline(deviceId));
        Assert.Null(Hub.GetDeviceSessionId(deviceId));

        // idle presence answers the heartbeat
        await device.SendAsync(new Frame { Ping = new Ping { TimestampMs = 42 } });
        Assert.Equal(42ul, (await device.ExpectAsync(Frame.BodyOneofCase.Pong)).Pong.TimestampMs);
    }

    [Fact]
    public async Task Enrollment_InvalidToken_IsRejected()
    {
        await using var peer = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        await peer.HelloAsync(PeerRole.Agent);
        await peer.SendAsync(new Frame
        {
            Authenticate = new Authenticate
            {
                Enrollment = new Enrollment { Token = "forged", DevicePublicKey = ByteString.CopyFrom(_deviceKey.ExportSubjectPublicKeyInfo()) },
            },
        });

        await peer.ExpectErrorAndCloseAsync(ErrorCodes.AuthFailed);
    }

    [Fact]
    public async Task Signature_WrongKey_IsRejected()
    {
        var deviceId = await EnrollAsync(await CreateGroupAsync(DeviceConsentPolicy.Ask));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var (device, first) = await ConnectDeviceAsync(deviceId, otherKey);
        await using var _ = device;

        Assert.Equal(ErrorCodes.AuthFailed, first?.Error?.Code);
        Assert.False(Hub.IsDeviceOnline(deviceId));
    }

    [Fact]
    public async Task Signature_PendingDevice_IsNotAuthorized_UntilApproved()
    {
        var groupId = (await Devices.CreateGroupAsync(_supporterId, "Strict", null, DeviceConsentPolicy.Ask, requireApproval: true)).Id!.Value;
        var deviceId = await EnrollAsync(groupId);

        var (pending, first) = await ConnectDeviceAsync(deviceId);
        await using (pending)
        {
            Assert.Equal(ErrorCodes.NotAuthorized, first?.Error?.Code);
        }

        Assert.Null(await Devices.ApproveAsync(_supporterId, deviceId));
        var (device, second) = await ConnectDeviceAsync(deviceId);
        await using var _ = device;
        Assert.True(second?.AuthResult?.Ok);
    }

    [Fact]
    public async Task AskPolicy_SupporterWithViewAndControl_ConsentPermissionsAndRouting()
    {
        var groupId = await CreateGroupAsync(DeviceConsentPolicy.Ask);
        Assert.Null(await Devices.SetPermissionAsync(_supporterId, groupId, _supporterId, DevicePermissions.Control));
        var deviceId = await EnrollAsync(groupId);
        var (device, _) = await ConnectDeviceAsync(deviceId);
        await using var deviceHandle = device;

        var (sessionId, error) = await Launcher.OpenAsync(_supporterId, isAdmin: false, deviceId);
        Assert.Null(error);
        var assigned = (await device.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionAssigned)).SessionAssigned;
        Assert.Equal(sessionId.ToString(), assigned.SessionId);
        Assert.Equal(ConsentPolicy.Ask, assigned.ConsentPolicy);
        Assert.Equal("Sam Supporter", assigned.OperatorName);
        Assert.Equal(sessionId, Hub.GetDeviceSessionId(deviceId));

        var (viewer, _) = await TestPeer.ConnectViewerAsync(_factory.Server, await TicketAsync(sessionId!.Value));
        await using var viewerHandle = viewer;

        var joined = (await device.ExpectEventuallyAsync(Frame.BodyOneofCase.ViewerJoined)).ViewerJoined;
        Assert.Equal(ViewAndControl, joined.Permissions);
        var request = (await device.ExpectEventuallyAsync(Frame.BodyOneofCase.ConsentRequest)).ConsentRequest;
        Assert.Equal(ViewAndControl, request.RequestedPermissions);
        await device.SendAsync(new Frame { ConsentResponse = new ConsentResponse { SessionId = request.SessionId, ViewerId = request.ViewerId, Granted = true } });
        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);

        // viewer → device: shell is not permitted (dropped), input is
        await viewer.SendAsync(DataFrame(sessionId.Value, Channel.Shell, 1, string.Empty));
        await viewer.SendAsync(DataFrame(sessionId.Value, Channel.Input, 1, string.Empty));
        Assert.Equal((uint)Channel.Input, (await device.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionData)).SessionData.Channel);

        // device → viewer: file offers are not permitted (dropped), video is
        await device.SendAsync(DataFrame(sessionId.Value, Channel.File, 1, string.Empty));
        await device.SendAsync(DataFrame(sessionId.Value, Channel.Video, 1, string.Empty));
        Assert.Equal((uint)Channel.Video, (await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionData)).SessionData.Channel);

        // ending the session keeps the device online (idle presence)
        await viewer.SendAsync(new Frame { EndSession = new EndSession { SessionId = sessionId.Value.ToString(), Reason = "done" } });
        await device.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        Assert.Equal(SessionStatus.Ended, await DbStatusAsync(sessionId.Value));
        await device.SendAsync(new Frame { Ping = new Ping { TimestampMs = 7 } });
        await device.ExpectEventuallyAsync(Frame.BodyOneofCase.Pong);
        Assert.True(Hub.IsDeviceOnline(deviceId));
        Assert.Null(Hub.GetDeviceSessionId(deviceId));
    }

    [Fact]
    public async Task NotifyPolicy_GrantsWithoutConsentRequest()
    {
        var groupId = await CreateGroupAsync(DeviceConsentPolicy.Notify);
        Assert.Null(await Devices.SetPermissionAsync(_supporterId, groupId, _supporterId, DevicePermissions.All));
        var deviceId = await EnrollAsync(groupId);
        var (device, _) = await ConnectDeviceAsync(deviceId);
        await using var deviceHandle = device;

        var (sessionId, _) = await Launcher.OpenAsync(_supporterId, isAdmin: false, deviceId);
        Assert.Equal(ConsentPolicy.Notify, (await device.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionAssigned)).SessionAssigned.ConsentPolicy);
        var (viewer, _) = await TestPeer.ConnectViewerAsync(_factory.Server, await TicketAsync(sessionId!.Value));
        await using var viewerHandle = viewer;

        await viewer.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Active);

        // the device was told who joined, but never asked
        Frame? frame;
        do
        {
            frame = await device.ReceiveAsync();
            Assert.NotNull(frame);
            Assert.NotEqual(Frame.BodyOneofCase.ConsentRequest, frame.BodyCase);
        }
        while (!(frame.BodyCase == Frame.BodyOneofCase.SessionState && frame.SessionState.Status == WireStatus.Active));
        Assert.Equal(SessionStatus.Active, await DbStatusAsync(sessionId.Value));
    }

    [Fact]
    public async Task Launcher_WithoutPermission_OrOffline_Refuses()
    {
        var groupId = await CreateGroupAsync(DeviceConsentPolicy.Ask);
        var deviceId = await EnrollAsync(groupId);

        Assert.Equal(DeviceSessionLauncher.ErrorNotAllowed, (await Launcher.OpenAsync(_supporterId, isAdmin: false, deviceId)).ErrorKey);
        Assert.Equal(DeviceSessionLauncher.ErrorOffline, (await Launcher.OpenAsync(_supporterId, isAdmin: true, deviceId)).ErrorKey);
    }

    [Fact]
    public async Task ViewerTicket_PermanentSession_RequiresGroupPermission()
    {
        var groupId = await CreateGroupAsync(DeviceConsentPolicy.Ask);
        var deviceId = await EnrollAsync(groupId);
        var (device, _) = await ConnectDeviceAsync(deviceId);
        await using var deviceHandle = device;
        var (sessionId, _) = await Launcher.OpenAsync(_supporterId, isAdmin: true, deviceId);

        // the session's owner has no permission as a non-admin: no ticket
        Assert.Null(await Sessions.IssueViewerTicketAsync(sessionId!.Value, _supporterId, userIsAdmin: false));
        Assert.NotNull(await Sessions.IssueViewerTicketAsync(sessionId.Value, _supporterId, userIsAdmin: true));
    }

    [Fact]
    public async Task DisablingTheDevice_EndsTheSession_AndDisconnects()
    {
        var groupId = await CreateGroupAsync(DeviceConsentPolicy.Silent);
        var deviceId = await EnrollAsync(groupId);
        var (device, _) = await ConnectDeviceAsync(deviceId);
        await using var deviceHandle = device;
        var (sessionId, _) = await Launcher.OpenAsync(_supporterId, isAdmin: true, deviceId);
        await device.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionAssigned);

        Assert.Null(await Devices.SetDisabledAsync(_supporterId, deviceId, disabled: true));

        await device.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionState, f => f.SessionState.Status == WireStatus.Ended);
        await device.ExpectErrorAndCloseAsync(ErrorCodes.NotAuthorized);
        Assert.Equal(SessionEndReasons.DeviceDisabled, await DbEndReasonAsync(sessionId!.Value));
        await WaitUntilAsync(() => !Hub.IsDeviceOnline(deviceId));
    }

    [Fact]
    public async Task ConnectionLoss_DuringSession_DeviceIsReboundAfterReconnect()
    {
        var groupId = await CreateGroupAsync(DeviceConsentPolicy.Silent);
        var deviceId = await EnrollAsync(groupId);
        var (device, _) = await ConnectDeviceAsync(deviceId);
        var (sessionId, _) = await Launcher.OpenAsync(_supporterId, isAdmin: true, deviceId);
        await device.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionAssigned);

        device.Abort();
        await device.DisposeAsync();
        await WaitUntilAsync(() => Hub.GetLiveStatus(sessionId!.Value) == SessionStatus.Suspended);

        var (again, first) = await ConnectDeviceAsync(deviceId);
        await using var _ = again;
        Assert.True(first?.AuthResult?.Ok);
        Assert.Equal(sessionId.ToString(), (await again.ExpectEventuallyAsync(Frame.BodyOneofCase.SessionAssigned)).SessionAssigned.SessionId);
        await WaitUntilAsync(() => Hub.GetLiveStatus(sessionId!.Value) == SessionStatus.WaitingForViewer);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Guid> CreateGroupAsync(DeviceConsentPolicy policy) =>
        (await Devices.CreateGroupAsync(_supporterId, "Customer " + Guid.NewGuid().ToString("N")[..6], null, policy, requireApproval: false)).Id!.Value;

    /// <summary>Enrolls the test key over the WebSocket and returns the new device id.</summary>
    private async Task<Guid> EnrollAsync(Guid groupId)
    {
        var token = (await Devices.CreateTokenAsync(_supporterId, groupId, null, TimeSpan.FromDays(1), 1))!;
        await using var peer = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        await peer.HelloAsync(PeerRole.Agent);
        await peer.SendAsync(new Frame
        {
            Authenticate = new Authenticate
            {
                Enrollment = new Enrollment
                {
                    Token = token.Token,
                    DevicePublicKey = ByteString.CopyFrom(_deviceKey.ExportSubjectPublicKeyInfo()),
                    Machine = new MachineInfo { MachineName = "SERVER-01", OsName = "Windows Server", OsVersion = "10.0.26100" },
                },
            },
        });

        var auth = (await peer.ExpectAsync(Frame.BodyOneofCase.AuthResult)).AuthResult;
        Assert.True(auth.Ok);
        Assert.Null(await peer.ReceiveAsync()); // the server closes after enrollment
        return Guid.Parse(auth.PeerId);
    }

    /// <summary>Connects as the device, signing the handshake nonce. Returns the first frame after Authenticate.</summary>
    private async Task<(TestPeer Peer, Frame? First)> ConnectDeviceAsync(Guid deviceId, ECDsa? key = null)
    {
        var peer = await TestPeer.ConnectAsync(_factory.Server, "/ws/agent");
        var ack = await peer.HelloAsync(PeerRole.Agent);
        var signature = (key ?? _deviceKey).SignData(ack.Nonce.ToByteArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        await peer.SendAsync(new Frame
        {
            Authenticate = new Authenticate { DeviceSignature = new DeviceSignature { DeviceId = deviceId.ToString(), Signature = ByteString.CopyFrom(signature) } },
        });
        return (peer, await peer.ReceiveAsync());
    }

    private async Task<string> TicketAsync(Guid sessionId) =>
        Assert.IsType<string>(await Sessions.IssueViewerTicketAsync(sessionId, _supporterId, userIsAdmin: false));

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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition(), "condition not reached in time");
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
