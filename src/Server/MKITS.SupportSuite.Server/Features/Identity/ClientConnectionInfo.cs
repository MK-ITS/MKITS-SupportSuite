// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace MKITS.SupportSuite.Server.Features.Identity;

/// <summary>
/// The client's IP address for the current scope. In interactive components there is no per-event HttpContext,
/// so the address is captured when the circuit connects (see <see cref="ClientConnectionCircuitHandler"/>).
/// </summary>
public sealed class ClientConnectionInfo(IHttpContextAccessor httpContextAccessor)
{
    private IPAddress? _captured;

    /// <summary>Client address (already resolved through trusted proxies), or <c>null</c> if unknown.</summary>
    public IPAddress? RemoteIp => _captured ?? httpContextAccessor.HttpContext?.Connection.RemoteIpAddress;

    internal void Capture(IPAddress? address) => _captured ??= address;
}

/// <summary>Captures the client address when an interactive circuit is established.</summary>
public sealed class ClientConnectionCircuitHandler(IHttpContextAccessor httpContextAccessor, ClientConnectionInfo info) : CircuitHandler
{
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        info.Capture(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress);
        return Task.CompletedTask;
    }
}
