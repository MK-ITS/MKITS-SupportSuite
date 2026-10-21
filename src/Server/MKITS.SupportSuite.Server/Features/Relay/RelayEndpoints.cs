// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Security.Cryptography;
using Google.Protobuf;
using MKITS.SupportSuite.Common;
using MKITS.SupportSuite.Common.Protocol;
using MKITS.SupportSuite.Common.Protocol.V0;
using MKITS.SupportSuite.Server.Features.Devices;
using MKITS.SupportSuite.Server.Features.IpBans;
using MKITS.SupportSuite.Server.Features.Sessions;

namespace MKITS.SupportSuite.Server.Features.Relay;

/// <summary>Registration of the relay (WebSocket endpoints and session broker).</summary>
public static class RelaySetup
{
    public static IServiceCollection AddMkitsRelay(this IServiceCollection services)
    {
        services.AddSingleton<SessionHub>();
        services.AddSingleton<RelayConnectionHandler>();
        services.AddSingleton<DeviceSessionLauncher>();
        services.AddHostedService<RelayMaintenance>();
        return services;
    }

    /// <summary>Maps <c>/ws/agent</c> and <c>/ws/viewer</c> (docs/protocol.md §1).</summary>
    public static IEndpointRouteBuilder MapRelayEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.Map("/ws/agent", (HttpContext context, RelayConnectionHandler handler) => handler.RunAsync(context, PeerRole.Agent))
            .DisableAntiforgery();
        endpoints.Map("/ws/viewer", (HttpContext context, RelayConnectionHandler handler) => handler.RunAsync(context, PeerRole.Viewer))
            .DisableAntiforgery();
        return endpoints;
    }
}

/// <summary>
/// Runs one WebSocket connection: handshake (<c>Hello</c> → <c>HelloAck</c> → <c>Authenticate</c>), then the
/// receive loop. Authentication failures count for the IP ban service. Cookies are never used on these
/// endpoints, so cross-site WebSocket hijacking is not possible; viewers authenticate with a one-time ticket.
/// </summary>
public sealed partial class RelayConnectionHandler(
    SessionHub hub,
    SessionService sessions,
    DeviceService devices,
    ViewerTicketStore viewerTickets,
    IpBanService ipBans,
    TimeProvider timeProvider,
    ILogger<RelayConnectionHandler> logger)
{
    internal static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan AuthenticateTimeout = TimeSpan.FromSeconds(30);

    public async Task RunAsync(HttpContext context, PeerRole role)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        if (!context.WebSockets.WebSocketRequestedProtocols.Contains(FrameCodec.SubProtocol, StringComparer.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            SubProtocol = FrameCodec.SubProtocol,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            DangerousEnableCompression = false, // compression of encrypted data is useless and enables CRIME-style attacks
        }).ConfigureAwait(false);

        using var peer = new PeerConnection(socket, role, context.Connection.RemoteIpAddress, timeProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, peer.Aborted);
        var sender = peer.RunSenderAsync();
        try
        {
            if (await HandshakeAsync(peer, lifetime.Token).ConfigureAwait(false))
            {
                await ReceiveLoopAsync(peer, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // connection aborted or server stopping
        }
        finally
        {
            peer.Complete();
            await hub.DetachAsync(peer).ConfigureAwait(false);
            await sender.ConfigureAwait(false);
            await peer.WaitForCloseAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> HandshakeAsync(PeerConnection peer, CancellationToken cancellationToken)
    {
        var hello = await ReceiveWithTimeoutAsync(peer, HelloTimeout, cancellationToken).ConfigureAwait(false);
        if (hello?.BodyCase != Frame.BodyOneofCase.Hello || hello.Hello.Role != peer.Role)
        {
            return Fail(peer, ErrorCodes.ProtocolMismatch, "expected Hello with matching role");
        }

        if (!ProtocolVersion.IsCompatible((int)hello.Hello.ProtocolMajor))
        {
            return Fail(peer, ErrorCodes.ProtocolMismatch, $"server speaks {ProtocolVersion.AsString}");
        }

        var nonce = RandomNumberGenerator.GetBytes(32);
        peer.Send(new Frame
        {
            HelloAck = new HelloAck
            {
                ServerVersion = ProductInfo.Version,
                Nonce = ByteString.CopyFrom(nonce),
                HeartbeatSeconds = (uint)SessionHub.HeartbeatInterval.TotalSeconds,
            },
        });

        var authenticate = await ReceiveWithTimeoutAsync(peer, AuthenticateTimeout, cancellationToken).ConfigureAwait(false);
        if (authenticate?.BodyCase != Frame.BodyOneofCase.Authenticate)
        {
            return Fail(peer, ErrorCodes.AuthFailed, "expected Authenticate");
        }

        var auth = authenticate.Authenticate;
        return (peer.Role, auth.MethodCase) switch
        {
            (PeerRole.Agent, Authenticate.MethodOneofCase.AdHocCode) => await AuthenticateAdHocAsync(peer, auth.AdHocCode, cancellationToken).ConfigureAwait(false),
            (PeerRole.Agent, Authenticate.MethodOneofCase.Resume) => await ResumeAsync(peer, auth.Resume, cancellationToken).ConfigureAwait(false),
            (PeerRole.Agent, Authenticate.MethodOneofCase.Enrollment) => await EnrollAsync(peer, auth.Enrollment, hello.Hello, cancellationToken).ConfigureAwait(false),
            (PeerRole.Agent, Authenticate.MethodOneofCase.DeviceSignature) => await AuthenticateDeviceAsync(peer, auth.DeviceSignature, nonce, hello.Hello, cancellationToken).ConfigureAwait(false),
            (PeerRole.Viewer, Authenticate.MethodOneofCase.ViewerTicket) => await AuthenticateViewerAsync(peer, auth.ViewerTicket, cancellationToken).ConfigureAwait(false),
            _ => Fail(peer, ErrorCodes.NotAuthorized, "authentication method not supported"),
        };
    }

    private async Task<bool> AuthenticateAdHocAsync(PeerConnection peer, AdHocCode code, CancellationToken cancellationToken)
    {
        var result = await sessions.RedeemCodeAsync(code.Code, new AgentInfo(code.Machine?.MachineName, peer.RemoteAddress), cancellationToken).ConfigureAwait(false);
        if (result.SessionId is not { } sessionId)
        {
            return Fail(peer, result.ErrorCode ?? ErrorCodes.CodeInvalid, "code rejected");
        }

        return await hub.AttachAgentAsync(peer, sessionId, result.OperatorName, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Registers a new permanent device (docs/protocol.md §3). The connection is closed afterwards; the agent
    /// reconnects with its key (<see cref="AuthenticateDeviceAsync"/>).
    /// </summary>
    private async Task<bool> EnrollAsync(PeerConnection peer, Enrollment enrollment, Hello hello, CancellationToken cancellationToken)
    {
        var machine = enrollment.Machine;
        var result = await devices.EnrollAsync(
            enrollment.Token,
            enrollment.DevicePublicKey.ToByteArray(),
            new DeviceInfo(machine?.MachineName, hello.Platform, machine?.OsName, machine?.OsVersion, hello.ProductVersion),
            peer.RemoteAddress,
            cancellationToken).ConfigureAwait(false);
        if (result.DeviceId is not { } deviceId)
        {
            return Fail(peer, result.ErrorCode ?? ErrorCodes.AuthFailed, "enrollment rejected");
        }

        peer.Send(new Frame { AuthResult = new AuthResult { Ok = true, PeerId = deviceId.ToString() } });
        peer.Complete();
        return false;
    }

    /// <summary>A permanent device proves possession of its key by signing this connection's nonce.</summary>
    private async Task<bool> AuthenticateDeviceAsync(PeerConnection peer, DeviceSignature signature, byte[] nonce, Hello hello, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(signature.DeviceId, out var deviceId))
        {
            await ipBans.RecordFailureAsync(peer.RemoteAddress, FailureKinds.DeviceSignature, cancellationToken).ConfigureAwait(false);
            return Fail(peer, ErrorCodes.AuthFailed, "device rejected");
        }

        var facts = await devices.AuthenticateAsync(
            deviceId, nonce, signature.Signature.ToByteArray(),
            new DeviceInfo(null, hello.Platform, null, null, hello.ProductVersion),
            peer.RemoteAddress, cancellationToken).ConfigureAwait(false);
        if (facts is null)
        {
            return Fail(peer, ErrorCodes.AuthFailed, "device rejected");
        }

        if (facts.Status != DeviceStatus.Active)
        {
            return Fail(peer, ErrorCodes.NotAuthorized, "device awaits approval");
        }

        return await hub.AttachDeviceAsync(peer, facts, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ResumeAsync(PeerConnection peer, Resume resume, CancellationToken cancellationToken)
    {
        if (await hub.ResumeAgentAsync(peer, resume.SessionId, resume.Token, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await ipBans.RecordFailureAsync(peer.RemoteAddress, FailureKinds.SessionCode, cancellationToken).ConfigureAwait(false);
        return Fail(peer, ErrorCodes.AuthFailed, "resume rejected");
    }

    private async Task<bool> AuthenticateViewerAsync(PeerConnection peer, ViewerTicket ticket, CancellationToken cancellationToken)
    {
        if (viewerTickets.Redeem(ticket.Ticket) is not { } grant)
        {
            await ipBans.RecordFailureAsync(peer.RemoteAddress, FailureKinds.ViewerTicket, cancellationToken).ConfigureAwait(false);
            return Fail(peer, ErrorCodes.AuthFailed, "ticket rejected");
        }

        return await hub.AttachViewerAsync(peer, grant, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(PeerConnection peer, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var (status, frame) = await peer.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            switch (status)
            {
                case ReceiveStatus.Frame:
                    await hub.HandleFrameAsync(peer, frame!, cancellationToken).ConfigureAwait(false);
                    break;
                case ReceiveStatus.TooLarge:
                    Fail(peer, ErrorCodes.MessageTooLarge, $"limit is {FrameCodec.MaxMessageSize} bytes");
                    return;
                case ReceiveStatus.Malformed:
                    LogMalformed(logger, peer.Role);
                    Fail(peer, ErrorCodes.ProtocolMismatch, "malformed frame");
                    return;
                default:
                    return; // closed
            }
        }
    }

    private static async Task<Frame?> ReceiveWithTimeoutAsync(PeerConnection peer, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var (status, frame) = await peer.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
            return status == ReceiveStatus.Frame ? frame : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // handshake timeout
        }
    }

    /// <summary>Sends an error and closes the connection after it was delivered. Always returns <c>false</c>.</summary>
    private static bool Fail(PeerConnection peer, string code, string detail)
    {
        peer.Send(SessionHub.ErrorFrame(code, detail));
        peer.Complete();
        return false;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Closing {Role} connection: malformed frame")]
    private static partial void LogMalformed(ILogger logger, PeerRole role);
}

/// <summary>Drives heartbeats and session timeouts.</summary>
public sealed partial class RelayMaintenance(SessionHub hub, TimeProvider timeProvider, ILogger<RelayMaintenance> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await hub.TickAsync(stoppingToken).ConfigureAwait(false);
                hub.TickIdleDevices();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogTickFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Relay maintenance failed")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
